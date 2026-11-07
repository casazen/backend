using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads an administrator PDF into a proposal for the condominium's fiscal year. The book changes only on confirm.
/// Manual budget and final entry stay on the condominium service.
/// </summary>
public sealed class AdministratorStatementReaderService(
    AppDbContext db,
    ICondominiumAccountingService condominiums,
    ILogger<AdministratorStatementReaderService> logger) : IAdministratorStatementReaderService
{
    private const int MaxFileBytes = 1_048_576;

    public async Task<StatementProposalView> ProposeAsync(
        HostScope scope, Guid condominiumId, StatementFileUpload file, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(file);
        RequireUser(userId);
        if (file.Content.Length is 0 or > MaxFileBytes || !IsPdf(file))
            throw new DomainRuleException(CondominiumErrorCodes.FileUnsupported, "CondominiumStatementFileUnsupported");

        var text = PdfText(file.Content);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        if (!AdministratorStatementReader.TryRead(text, condominium.FiscalStartMonth, out var reading, out _) || reading is null)
            throw new DomainRuleException(CondominiumErrorCodes.FileInvalid, "CondominiumStatementUnreadable");

        var hash = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant();
        var existing = await db.CondominiumStatementProposals
            .FirstOrDefaultAsync(
                item => item.OrgId == scope.OrgId && item.CondominiumId == condominiumId && item.ContentHash == hash && item.Status == CondominiumStatementProposalStatus.Proposed,
                cancellationToken);
        if (existing is not null)
            return ToView(existing);

        var periodId = await db.CondominiumPeriods
            .Where(item => item.CondominiumId == condominiumId && item.OrgId == scope.OrgId && item.Label == reading.Label)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var proposal = new CondominiumStatementProposal
        {
            OrgId = scope.OrgId,
            CondominiumId = condominiumId,
            ContentHash = hash,
            FileName = Trim(file.FileName, CondominiumStatementProposal.FileNameMaxLength),
            Kind = reading.Kind,
            Label = reading.Label,
            PeriodId = periodId,
            OrdinaryAmount = reading.Ordinary,
            ExtraordinaryAmount = reading.Extraordinary,
            ExtraordinaryDescription = reading.ExtraordinaryDescription,
            FinalAmount = reading.FinalAmount,
            Settlement = reading.Settlement,
            Confidence = reading.Confidence,
            ChargesTenant = reading.ChargesTenant,
            Excerpt = reading.Excerpt,
        };
        db.CondominiumStatementProposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Administrator statement {ProposalId} proposed for condominium {CondominiumId}", proposal.Id, condominiumId);
        return ToView(proposal);
    }

    public async Task<StatementProposalView> ConfirmAsync(
        HostScope scope, Guid condominiumId, Guid proposalId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var proposal = await db.CondominiumStatementProposals
            .FirstOrDefaultAsync(item => item.Id == proposalId && item.CondominiumId == condominiumId && item.OrgId == scope.OrgId, cancellationToken)
            ?? throw new NotFoundException("Proposal not found")
            {
                Code = CondominiumErrorCodes.FileInvalid,
                MessageKey = "CondominiumStatementProposalNotFound",
            };
        if (proposal.Status == CondominiumStatementProposalStatus.Confirmed)
            return ToView(proposal);

        var period = await condominiums.CreatePeriodAsync(
            scope, condominiumId, new CreateCondominiumPeriodCommand(proposal.Label), userId, cancellationToken);
        Guid statementId;
        if (proposal.Kind == CondominiumStatementKind.Budget)
        {
            if (proposal.OrdinaryAmount is not > 0m)
                throw new DomainRuleException(CondominiumErrorCodes.FileInvalid, "CondominiumStatementUnreadable");
            var budget = await condominiums.ConfirmBudgetAsync(
                scope,
                condominiumId,
                period.Id,
                new ConfirmBudgetCommand(
                    proposal.OrdinaryAmount.Value,
                    "Preventivo",
                    proposal.ExtraordinaryAmount is > 0m
                        ? [new ExtraordinaryExpenseCommand(proposal.ExtraordinaryAmount.Value, proposal.ExtraordinaryDescription)]
                        : [],
                    CondominiumInstallmentCadence.Monthly,
                    OrdinaryNeedsReview: !proposal.ChargesTenant),
                userId,
                cancellationToken);
            statementId = budget.Statement.Id;
        }
        else
        {
            if (proposal.FinalAmount is not > 0m)
                throw new DomainRuleException(CondominiumErrorCodes.FileInvalid, "CondominiumStatementUnreadable");
            var recoverability = proposal.ChargesTenant ? TenantRecoverability.Recoverable : TenantRecoverability.NeedsReview;
            var review = proposal.ChargesTenant ? ExpenseReviewStatus.Accepted : ExpenseReviewStatus.Pending;
            var final = await condominiums.ConfirmFinalAsync(
                scope,
                condominiumId,
                period.Id,
                new ConfirmFinalCommand(
                    proposal.FinalAmount.Value,
                    proposal.Settlement ?? PriorYearBalanceDisposition.CarryForward,
                    [new FinalExpenseLineCommand("Consuntivo", proposal.FinalAmount.Value, CondominiumExpenseNature.Ordinary, recoverability, null, proposal.Confidence, ExpenseClassificationSource.Document, null, null, review)]),
                userId,
                cancellationToken);
            statementId = final.Statement.Id;
        }

        proposal.PeriodId = period.Id;
        proposal.StatementId = statementId;
        proposal.Status = CondominiumStatementProposalStatus.Confirmed;
        proposal.ConfirmedAt = DateTime.UtcNow;
        proposal.ConfirmedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Administrator statement {ProposalId} confirmed into period {PeriodId}", proposal.Id, period.Id);
        return ToView(proposal);
    }

    private async Task<Condominium> RequireCondominiumAsync(HostScope scope, Guid condominiumId, CancellationToken cancellationToken)
    {
        var condominium = await db.Condominiums
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == condominiumId && item.OrgId == scope.OrgId, cancellationToken)
            ?? throw new NotFoundException("Condominium not found")
            {
                Code = CondominiumErrorCodes.NotFound,
                MessageKey = "CondominiumNotFound",
            };
        if (scope.IsOrgWide)
            return condominium;

        var visible = await db.CondominiumUnits
            .Where(unit => unit.CondominiumId == condominium.Id)
            .Select(unit => unit.Property)
            .InScope(scope)
            .AnyAsync(cancellationToken);
        if (!visible)
            throw new NotFoundException("Condominium not found")
            {
                Code = CondominiumErrorCodes.NotFound,
                MessageKey = "CondominiumNotFound",
            };
        return condominium;
    }

    private static StatementProposalView ToView(CondominiumStatementProposal proposal) =>
        new(
            proposal.Id,
            proposal.PeriodId,
            proposal.Label,
            proposal.Kind,
            proposal.OrdinaryAmount,
            proposal.ExtraordinaryAmount,
            proposal.ExtraordinaryDescription,
            proposal.FinalAmount,
            proposal.Settlement,
            proposal.Confidence,
            proposal.ChargesTenant,
            proposal.Excerpt,
            AdministratorStatementReader.NeverExtracted,
            proposal.Status == CondominiumStatementProposalStatus.Confirmed,
            proposal.StatementId);

    private static string? PdfText(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            return string.Join('\n', document.GetPages().Select(page => page.Text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsPdf(StatementFileUpload file) =>
        file.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true
        || (file.Content.Length >= 4 && file.Content[0] == (byte)'%' && file.Content[1] == (byte)'P');

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : text[..max];
    }

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > CondominiumStatementProposal.UserIdMaxLength)
            throw new DomainRuleException(CondominiumErrorCodes.FileInvalid, "CondominiumStatementUnreadable");
    }
}
