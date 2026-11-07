using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Documents;
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
/// CondoLedger's year, adapted to CasaZen: preventivo and consuntivo, rates left as issued, extraordinary kept apart,
/// and the prior-year balance settled only the way the owner chooses.
/// </summary>
public sealed class CondominiumAccountingService(
    AppDbContext db,
    IPdfDocumentRenderer pdfRenderer,
    ILogger<CondominiumAccountingService> logger,
    TimeProvider? timeProvider = null) : ICondominiumAccountingService
{
    private const int MaxFileBytes = 1_048_576;

    private DateTime UtcNow => timeProvider?.GetUtcNow().UtcDateTime ?? DateTime.UtcNow;

    public async Task<CondominiumView> CreateCondominiumAsync(
        HostScope scope, CreateCondominiumCommand command, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsOrgWide)
            throw new DomainForbiddenException(CondominiumErrorCodes.NeedsWholeOrg, "CondominiumNeedsWholeOrg");
        var name = RequireText(command.Name, Condominium.NameMaxLength, "condominium_statement_invalid", "CondominiumStatementInvalid");
        if (!FiscalYear.IsValidStartMonth(command.FiscalStartMonth))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
        RequireUser(userId);
        var email = TrimToNull(command.AdministratorEmail);
        if (email is not null && (email.Length > Condominium.AdministratorEmailMaxLength || !email.Contains('@', StringComparison.Ordinal)))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");

        var now = UtcNow;
        var condominium = new Condominium
        {
            OrgId = scope.OrgId,
            Name = name,
            AdministratorName = TrimToLength(command.AdministratorName, Condominium.AdministratorNameMaxLength),
            AdministratorEmail = email,
            AdministratorPhone = TrimToLength(command.AdministratorPhone, Condominium.AdministratorPhoneMaxLength),
            FiscalStartMonth = command.FiscalStartMonth,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Condominiums.Add(condominium);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Condominium {CondominiumId} created", condominium.Id);
        return new CondominiumView(condominium.Id, condominium.Name, condominium.FiscalStartMonth);
    }

    public async Task<CondominiumUnitView> LinkPropertyAsync(
        HostScope scope, Guid condominiumId, LinkPropertyCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var property = await db.Properties
            .Where(p => p.Id == command.PropertyId && p.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Property not found")
            {
                Code = CondominiumErrorCodes.PropertyNotFound,
                MessageKey = "CondominiumPropertyNotFound",
            };

        if (command.ValidTo < command.ValidFrom)
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
        if (command.Millesimi is { } millesimi && (millesimi < 0m || millesimi > 1000m || !CondominiumMoney.IsCents(millesimi)))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");

        var links = await db.CondominiumUnits
            .Where(unit => unit.OrgId == scope.OrgId && unit.PropertyId == property.Id)
            .ToListAsync(cancellationToken);
        if (links.Any(unit => ValidityOverlaps(unit.ValidFrom, unit.ValidTo, command.ValidFrom, command.ValidTo)))
            throw new DomainConflictException(CondominiumErrorCodes.PeriodOverlap, "CondominiumPeriodOverlap");

        var now = UtcNow;
        var unit = new CondominiumUnit
        {
            OrgId = scope.OrgId,
            CondominiumId = condominium.Id,
            PropertyId = property.Id,
            UnitReference = TrimToLength(command.UnitReference, CondominiumUnit.UnitReferenceMaxLength),
            Millesimi = command.Millesimi,
            ValidFrom = command.ValidFrom,
            ValidTo = command.ValidTo,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CondominiumUnits.Add(unit);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new CondominiumUnitView(unit.Id, unit.PropertyId, unit.UnitReference, unit.Millesimi);
    }

    public async Task<CondominiumPeriodView> CreatePeriodAsync(
        HostScope scope, Guid condominiumId, CreateCondominiumPeriodCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await EnsurePeriodAsync(condominium, command.Label, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return ToPeriod(period);
    }

    public async Task<BudgetConfirmation> ConfirmBudgetAsync(
        HostScope scope, Guid condominiumId, Guid periodId, ConfirmBudgetCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await RequirePeriodAsync(condominium, periodId, cancellationToken);
        EnsureOpen(period);
        RequireUser(userId);
        if (command.OrdinaryAmount <= 0m || !CondominiumMoney.InRange(command.OrdinaryAmount))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");

        var extraordinary = new List<(decimal Amount, string Description)>();
        foreach (var line in command.Extraordinary ?? [])
        {
            if (line.Amount <= 0m || !CondominiumMoney.InRange(line.Amount))
                throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
            extraordinary.Add((line.Amount, string.IsNullOrWhiteSpace(line.Description) ? "Spesa straordinaria" : line.Description.Trim()));
        }

        var unitId = await FirstUnitIdAsync(condominium.Id, cancellationToken);
        var statement = await AddStatementAsync(
            period,
            CondominiumStatementKind.Budget,
            command.OrdinaryAmount + extraordinary.Sum(line => line.Amount),
            userId,
            Lines(),
            cancellationToken);
        if (period.Status is CondominiumPeriodStatus.Draft or CondominiumPeriodStatus.Reopened)
            period.Status = CondominiumPeriodStatus.BudgetApproved;

        var existing = await LoadInstallmentsAsync(period.Id, cancellationToken);
        var leftAsIssued = existing.Any(installment => !IsPriorOnly(installment));
        if (!leftAsIssued)
        {
            var prior = await db.CondominiumPriorBalances
                .AsNoTracking()
                .SingleOrDefaultAsync(balance => balance.CondominiumPeriodId == period.Id, cancellationToken);
            var priorOnFirst = prior is { Disposition: PriorYearBalanceDisposition.NextInstallment } && !existing.Any(IsPriorOnly)
                ? prior.Amount
                : 0m;
            var offset = existing.Count == 0 ? 0 : existing.Max(installment => installment.SlotIndex) + 1;
            var generated = OwnerInstallmentPlan.Generate(
                period.StartDate, command.Cadence, command.OrdinaryAmount, extraordinary.Sum(line => line.Amount), priorOnFirst);
            var now = UtcNow;
            foreach (var row in generated)
            {
                db.CondominiumInstallments.Add(new CondominiumInstallment
                {
                    OrgId = period.OrgId,
                    CondominiumPeriodId = period.Id,
                    SlotIndex = offset + row.SlotIndex,
                    Label = row.Label,
                    PeriodStart = row.PeriodStart,
                    DueDate = row.DueDate,
                    OrdinaryAmount = row.Ordinary,
                    PriorSettlementAmount = row.Prior,
                    ExtraordinaryAmount = row.Extraordinary,
                    TotalAmount = row.Total,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        var installments = await LoadInstallmentsAsync(period.Id, cancellationToken);
        logger.LogInformation(
            "Budget of period {PeriodId} confirmed (revision {Revision}, installments left as issued: {Left})",
            period.Id, statement.RevisionNumber, leftAsIssued);
        return new BudgetConfirmation(ToStatement(statement), leftAsIssued, installments.Select(ToInstallment).ToList());

        IReadOnlyList<CondominiumExpenseLine> Lines()
        {
            var lines = new List<CondominiumExpenseLine>
            {
                Line(period, unitId, string.IsNullOrWhiteSpace(command.Description) ? "Preventivo ordinario" : command.Description.Trim(), "ordinario", command.OrdinaryAmount, CondominiumExpenseNature.Ordinary, null),
            };
            lines.AddRange(extraordinary.Select(item =>
                Line(period, unitId, item.Description, "straordinario", item.Amount, CondominiumExpenseNature.Extraordinary, null)));
            return lines;
        }
    }

    public async Task<FinalConfirmation> ConfirmFinalAsync(
        HostScope scope, Guid condominiumId, Guid periodId, ConfirmFinalCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await RequirePeriodAsync(condominium, periodId, cancellationToken);
        EnsureOpen(period);
        RequireUser(userId);
        if (!Enum.IsDefined(command.Settlement) || command.Amount <= 0m || !CondominiumMoney.InRange(command.Amount))
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");

        var unitId = await FirstUnitIdAsync(condominium.Id, cancellationToken);
        var statement = await AddStatementAsync(
            period,
            CondominiumStatementKind.Final,
            command.Amount,
            userId,
            [Line(period, unitId, $"Consuntivo {period.Label}", "consuntivo", command.Amount, CondominiumExpenseNature.Ordinary, TenantRecoverability.OwnerOnly)],
            cancellationToken);
        period.Status = CondominiumPeriodStatus.FinalStatementReceived;

        var extraordinary = await CurrentExtraordinaryAsync(period.Id, cancellationToken);
        var prior = await db.CondominiumPriorBalances.AsNoTracking()
            .SingleOrDefaultAsync(balance => balance.CondominiumPeriodId == period.Id, cancellationToken);
        var paid = await db.CondominiumPayments.AsNoTracking()
            .Where(payment => payment.CondominiumPeriodId == period.Id)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        var settlement = Conguaglio.Compute(command.Amount, extraordinary, prior?.Amount ?? 0m, paid);

        PriorYearBalanceView? nextBalance = null;
        if (settlement is { Direction: Conguaglio.Direction.Even })
        {
            await ClearCarriedBalanceAsync(condominium, period, cancellationToken);
        }
        else if (settlement is not null)
        {
            if (command.Settlement == PriorYearBalanceDisposition.Refund && settlement.Direction != Conguaglio.Direction.Credit)
                throw new DomainRuleException(CondominiumErrorCodes.PriorBalanceInvalid, "CondominiumPriorBalanceInvalid");

            var next = await EnsureNextPeriodAsync(condominium, period, cancellationToken);
            nextBalance = await SavePriorAsync(next, period.Id, settlement.Amount, command.Settlement, $"Conguaglio {period.Label}", userId, cancellationToken);
            await ApplyRecoveryAsync(next, settlement.Amount, command.Settlement, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Final statement of period {PeriodId} confirmed (revision {Revision})", period.Id, statement.RevisionNumber);
        return new FinalConfirmation(ToStatement(statement), nextBalance);
    }

    public async Task<PriorYearBalanceView> RecordPriorYearBalanceAsync(
        HostScope scope, Guid condominiumId, Guid periodId, RecordPriorYearBalanceCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await RequirePeriodAsync(condominium, periodId, cancellationToken);
        EnsureOpen(period);
        RequireUser(userId);
        if (command.Amount == 0m || !CondominiumMoney.InRange(command.Amount) || !Enum.IsDefined(command.Disposition))
            throw new DomainRuleException(CondominiumErrorCodes.PriorBalanceInvalid, "CondominiumPriorBalanceInvalid");
        if (command.Disposition == PriorYearBalanceDisposition.Refund && command.Amount > 0m)
            throw new DomainRuleException(CondominiumErrorCodes.PriorBalanceInvalid, "CondominiumPriorBalanceInvalid");
        if (command.SourcePeriodId is { } sourceId)
            await RequirePeriodAsync(condominium, sourceId, cancellationToken);

        var view = await SavePriorAsync(period, command.SourcePeriodId, command.Amount, command.Disposition, command.Description, userId, cancellationToken);
        await ApplyRecoveryAsync(period, command.Amount, command.Disposition, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return view;
    }

    public async Task<CondominiumPaymentView> RecordPaymentAsync(
        HostScope scope, Guid condominiumId, Guid periodId, RecordCondominiumPaymentCommand command, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(condominiumId, cancellationToken);
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await RequirePeriodAsync(condominium, periodId, cancellationToken);
        RequireUser(userId);
        if (command.Amount <= 0m || !CondominiumMoney.InRange(command.Amount))
            throw new DomainRuleException(CondominiumErrorCodes.PaymentInvalid, "CondominiumPaymentInvalid");

        var hash = TrimToNull(command.ExternalSourceHash);
        if (hash is not null && hash.Length > CondominiumPayment.ExternalSourceHashMaxLength)
            throw new DomainRuleException(CondominiumErrorCodes.PaymentInvalid, "CondominiumPaymentInvalid");
        if (hash is not null)
        {
            var existing = await db.CondominiumPayments
                .FirstOrDefaultAsync(payment => payment.OrgId == scope.OrgId && payment.ExternalSourceHash == hash, cancellationToken);
            if (existing is not null)
            {
                if (existing.Amount == command.Amount && existing.CondominiumPeriodId == period.Id && existing.PaidOn == command.PaidOn)
                    return new CondominiumPaymentView(existing.Id, existing.Amount, existing.PaidOn, existing.ExternalSourceHash, true);
                throw new DomainConflictException(CondominiumErrorCodes.PaymentDuplicate, "CondominiumPaymentDuplicate");
            }
        }

        Guid? installmentId = null;
        if (command.InstallmentId is { } requested)
        {
            var installment = await db.CondominiumInstallments
                .FirstOrDefaultAsync(row => row.Id == requested && row.CondominiumPeriodId == period.Id, cancellationToken)
                ?? throw new NotFoundException("Installment not found")
                {
                    Code = CondominiumErrorCodes.InstallmentNotFound,
                    MessageKey = "CondominiumInstallmentNotFound",
                };
            if (installment.Status == CondominiumInstallmentStatus.Cancelled)
                throw new DomainRuleException(CondominiumErrorCodes.PaymentInvalid, "CondominiumPaymentInvalid");
            installmentId = installment.Id;
        }

        Guid? priorId = null;
        if (command.CoversPriorBalance)
        {
            priorId = await db.CondominiumPriorBalances
                .Where(balance => balance.CondominiumPeriodId == period.Id)
                .Select(balance => (Guid?)balance.Id)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new DomainRuleException(CondominiumErrorCodes.PriorBalanceInvalid, "CondominiumPriorBalanceInvalid");
        }

        var method = TrimToLength(command.Method, CondominiumPayment.MethodMaxLength);
        var payment = new CondominiumPayment
        {
            OrgId = scope.OrgId,
            CondominiumPeriodId = period.Id,
            CondominiumInstallmentId = installmentId,
            PriorBalanceId = priorId,
            Amount = command.Amount,
            PaidOn = command.PaidOn,
            Method = method,
            BankMovementId = command.BankMovementId,
            ExternalSourceHash = hash,
            Note = TrimToLength(command.Note, CondominiumPayment.NoteMaxLength),
            RecordedByUserId = userId,
            RecordedAt = UtcNow,
        };
        db.CondominiumPayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshInstallmentStatusAsync(period.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new CondominiumPaymentView(payment.Id, payment.Amount, payment.PaidOn, payment.ExternalSourceHash, false);
    }

    public async Task<SituazioneReport.Year> GetReportAsync(
        HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default)
    {
        var condominium = await RequireCondominiumAsync(scope, condominiumId, cancellationToken);
        var period = await RequirePeriodAsync(condominium, periodId, cancellationToken);
        return await BuildYearAsync(condominium, period, cancellationToken);
    }

    public async Task<byte[]> RenderPaidPdfAsync(
        HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default)
    {
        var year = await GetReportAsync(scope, condominiumId, periodId, cancellationToken);
        return pdfRenderer.Render(SituazioneDocuments.Paid(year));
    }

    public async Task<byte[]> RenderUnpaidPdfAsync(
        HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default)
    {
        var year = await GetReportAsync(scope, condominiumId, periodId, cancellationToken);
        return pdfRenderer.Render(SituazioneDocuments.Unpaid(year));
    }

    public async Task<StatementImportResult> ImportStatementFileAsync(
        HostScope scope, Guid condominiumId, StatementFileUpload file, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Content.Length is 0 or > MaxFileBytes)
            throw new DomainRuleException(CondominiumErrorCodes.FileUnsupported, "CondominiumStatementFileUnsupported");

        var text = IsPdf(file) ? PdfText(file.Content) : System.Text.Encoding.UTF8.GetString(file.Content);
        if (!StatementFile.HasMarker(text))
            throw new DomainRuleException(CondominiumErrorCodes.FileUnsupported, "CondominiumStatementFileUnsupported");
        if (!StatementFile.TryParse(text, out var model, out var error) || model is null)
            throw new DomainRuleException(CondominiumErrorCodes.FileInvalid, "CondominiumStatementFileInvalid", error ?? "file");

        var period = await CreatePeriodAsync(scope, condominiumId, new CreateCondominiumPeriodCommand(model.Label), userId, cancellationToken);
        if (model.Kind == CondominiumStatementKind.Budget)
        {
            var confirmation = await ConfirmBudgetAsync(
                scope,
                condominiumId,
                period.Id,
                new ConfirmBudgetCommand(
                    model.Ordinary,
                    "Preventivo",
                    model.Extraordinary > 0m
                        ? [new ExtraordinaryExpenseCommand(model.Extraordinary, model.ExtraordinaryDescription)]
                        : [],
                    CondominiumInstallmentCadence.Monthly),
                userId,
                cancellationToken);
            return new StatementImportResult(period.Id, model.Kind, confirmation.Statement.Id);
        }

        var final = await ConfirmFinalAsync(
            scope,
            condominiumId,
            period.Id,
            new ConfirmFinalCommand(model.FinalAmount!.Value, model.Settlement ?? PriorYearBalanceDisposition.CarryForward),
            userId,
            cancellationToken);
        return new StatementImportResult(period.Id, model.Kind, final.Statement.Id);
    }

    private async Task<SituazioneReport.Year> BuildYearAsync(Condominium condominium, CondominiumPeriod period, CancellationToken cancellationToken)
    {
        var budget = await CurrentLinesAsync(period.Id, CondominiumStatementKind.Budget, cancellationToken);
        var final = await CurrentLinesAsync(period.Id, CondominiumStatementKind.Final, cancellationToken);
        var ordinary = budget.Where(line => line.ExpenseNature == CondominiumExpenseNature.Ordinary).Sum(line => line.Amount);
        var extraordinary = budget.Where(line => line.ExpenseNature == CondominiumExpenseNature.Extraordinary).Sum(line => line.Amount);
        var finalAmount = final.Count == 0 ? (decimal?)null : final.Sum(line => line.Amount);
        var prior = await db.CondominiumPriorBalances.AsNoTracking()
            .SingleOrDefaultAsync(balance => balance.CondominiumPeriodId == period.Id, cancellationToken);
        var installments = await LoadInstallmentsAsync(period.Id, cancellationToken);
        var payments = await db.CondominiumPayments.AsNoTracking()
            .Where(payment => payment.CondominiumPeriodId == period.Id)
            .OrderBy(payment => payment.PaidOn)
            .ToListAsync(cancellationToken);

        var allocated = SituazioneReport.Allocate(
            installments.Select(installment => (installment.Id, installment.DueDate, installment.TotalAmount)).ToList(),
            payments.Select(payment => ((Guid?)payment.CondominiumInstallmentId, payment.Amount)).ToList());
        var lines = installments.Select(installment => new SituazioneReport.InstallmentLine(
            installment.Id,
            installment.SlotIndex,
            installment.Label,
            installment.DueDate,
            installment.OrdinaryAmount,
            installment.PriorSettlementAmount,
            installment.ExtraordinaryAmount,
            installment.TotalAmount,
            allocated[installment.Id])).ToList();

        var paymentLines = payments.Select(payment => new SituazioneReport.PaymentLine(
            payment.Id,
            payment.PaidOn,
            payment.Method,
            payment.Amount,
            Covering(payment, installments))).ToList();

        return SituazioneReport.Build(
            condominium.Name,
            period.Label,
            period.StartDate,
            period.EndDate,
            ordinary,
            extraordinary,
            prior?.Amount ?? 0m,
            finalAmount,
            lines,
            paymentLines);
    }

    private static string Covering(CondominiumPayment payment, IReadOnlyList<CondominiumInstallment> installments)
    {
        if (payment.PriorBalanceId is not null)
            return "Conguaglio anno precedente";
        var linked = installments.FirstOrDefault(installment => installment.Id == payment.CondominiumInstallmentId);
        return linked?.Label ?? "Esercizio";
    }

    private async Task ApplyRecoveryAsync(
        CondominiumPeriod period, decimal amount, PriorYearBalanceDisposition disposition, CancellationToken cancellationToken)
    {
        var existing = await LoadInstallmentsAsync(period.Id, cancellationToken);
        var issued = existing.Select(installment => new OwnerInstallmentPlan.Row(
            installment.SlotIndex,
            installment.Label,
            installment.PeriodStart,
            installment.DueDate,
            installment.OrdinaryAmount,
            installment.PriorSettlementAmount,
            installment.ExtraordinaryAmount,
            installment.Status == CondominiumInstallmentStatus.Paid)).ToList();
        var updated = OwnerInstallmentPlan.ApplyRecovery(issued, amount, disposition, FiscalYear.SeparateBalanceDueDate(period.StartDate));
        var kept = updated.Select(row => row.SlotIndex).ToHashSet();
        var now = UtcNow;
        foreach (var installment in existing.Where(installment => !kept.Contains(installment.SlotIndex)))
        {
            installment.PriorSettlementAmount = 0m;
            installment.TotalAmount = installment.OrdinaryAmount + installment.ExtraordinaryAmount;
            installment.Status = CondominiumInstallmentStatus.Cancelled;
            installment.UpdatedAt = now;
        }

        foreach (var row in updated)
        {
            var installment = existing.FirstOrDefault(item => item.SlotIndex == row.SlotIndex);
            if (installment is null)
            {
                db.CondominiumInstallments.Add(new CondominiumInstallment
                {
                    OrgId = period.OrgId,
                    CondominiumPeriodId = period.Id,
                    SlotIndex = row.SlotIndex,
                    Label = row.Label,
                    PeriodStart = row.PeriodStart,
                    DueDate = row.DueDate,
                    OrdinaryAmount = row.Ordinary,
                    PriorSettlementAmount = row.Prior,
                    ExtraordinaryAmount = row.Extraordinary,
                    TotalAmount = row.Total,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                continue;
            }

            installment.PriorSettlementAmount = row.Prior;
            installment.TotalAmount = installment.OrdinaryAmount + row.Prior + installment.ExtraordinaryAmount;
            installment.UpdatedAt = now;
        }

        await RefreshInstallmentStatusAsync(period.Id, cancellationToken);
    }

    private async Task ClearCarriedBalanceAsync(Condominium condominium, CondominiumPeriod period, CancellationToken cancellationToken)
    {
        var nextLabel = NextLabel(condominium, period);
        var next = await db.CondominiumPeriods
            .SingleOrDefaultAsync(item => item.CondominiumId == condominium.Id && item.Label == nextLabel, cancellationToken);
        if (next is null)
            return;

        var balance = await db.CondominiumPriorBalances
            .SingleOrDefaultAsync(item => item.CondominiumPeriodId == next.Id && item.SourcePeriodId == period.Id, cancellationToken);
        if (balance is null)
            return;

        db.CondominiumPriorBalances.Remove(balance);
        await ApplyRecoveryAsync(next, 0m, PriorYearBalanceDisposition.CarryForward, cancellationToken);
    }

    private static string NextLabel(Condominium condominium, CondominiumPeriod period) =>
        condominium.FiscalStartMonth == 1
            ? (period.StartDate.Year + 1).ToString()
            : $"{period.StartDate.Year + 1}/{period.StartDate.Year + 2}";

    private async Task<PriorYearBalanceView> SavePriorAsync(
        CondominiumPeriod period,
        Guid? sourcePeriodId,
        decimal amount,
        PriorYearBalanceDisposition disposition,
        string? description,
        string userId,
        CancellationToken cancellationToken)
    {
        var balance = await db.CondominiumPriorBalances
            .SingleOrDefaultAsync(row => row.CondominiumPeriodId == period.Id, cancellationToken);
        if (balance is null)
        {
            balance = new CondominiumPriorBalance
            {
                OrgId = period.OrgId,
                CondominiumPeriodId = period.Id,
            };
            db.CondominiumPriorBalances.Add(balance);
        }

        balance.SourcePeriodId = sourcePeriodId;
        balance.Amount = amount;
        balance.Disposition = disposition;
        balance.Description = TrimToLength(description, CondominiumPriorBalance.DescriptionMaxLength);
        balance.DecidedByUserId = userId;
        balance.DecidedAt = UtcNow;
        return new PriorYearBalanceView(balance.Id, period.Id, balance.Amount, balance.Disposition);
    }

    private async Task<CondominiumStatement> AddStatementAsync(
        CondominiumPeriod period,
        CondominiumStatementKind kind,
        decimal total,
        string userId,
        IReadOnlyList<CondominiumExpenseLine> lines,
        CancellationToken cancellationToken)
    {
        var current = await db.CondominiumStatements
            .Where(statement => statement.CondominiumPeriodId == period.Id && statement.Kind == kind && statement.Status == CondominiumStatementStatus.Confirmed)
            .ToListAsync(cancellationToken);
        var now = UtcNow;
        foreach (var previous in current)
        {
            previous.Status = CondominiumStatementStatus.Superseded;
            previous.UpdatedAt = now;
        }

        var revision = await db.CondominiumStatements
            .Where(statement => statement.CondominiumPeriodId == period.Id && statement.Kind == kind)
            .Select(statement => (int?)statement.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;
        var statement = new CondominiumStatement
        {
            OrgId = period.OrgId,
            CondominiumPeriodId = period.Id,
            Kind = kind,
            RevisionNumber = revision + 1,
            Status = CondominiumStatementStatus.Confirmed,
            TotalAmount = CondominiumMoney.Round(total),
            ConfirmedAt = now,
            ConfirmedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            Lines = lines.ToList(),
        };
        db.CondominiumStatements.Add(statement);
        period.Version += 1;
        period.UpdatedAt = now;
        return statement;
    }

    private CondominiumExpenseLine Line(
        CondominiumPeriod period,
        Guid? unitId,
        string description,
        string category,
        decimal amount,
        CondominiumExpenseNature nature,
        TenantRecoverability? recoverability)
    {
        var classification = ExpenseClassification.Apply(
            nature, amount, recoverability, null, ExpenseClassificationSource.Manual, null);
        return new CondominiumExpenseLine
        {
            OrgId = period.OrgId,
            CondominiumUnitId = unitId,
            Description = description.Length > CondominiumExpenseLine.DescriptionMaxLength
                ? description[..CondominiumExpenseLine.DescriptionMaxLength]
                : description,
            Category = category,
            Amount = amount,
            CompetenceStart = period.StartDate,
            CompetenceEnd = period.EndDate,
            ExpenseNature = nature,
            TenantRecoverability = classification.Recoverability,
            RecoverablePercentage = classification.Percentage,
            RecoverableAmount = classification.RecoverableAmount,
            ClassificationSource = ExpenseClassificationSource.Manual,
            ReviewStatus = classification.ReviewStatus,
        };
    }

    private async Task<CondominiumPeriod> EnsurePeriodAsync(Condominium condominium, string? label, CancellationToken cancellationToken)
    {
        if (!FiscalYear.TryParseLabel(label, condominium.FiscalStartMonth, out var bounds, out _))
            throw new DomainRuleException(CondominiumErrorCodes.PeriodInvalid, "CondominiumPeriodInvalid");

        var existing = await db.CondominiumPeriods
            .SingleOrDefaultAsync(period => period.CondominiumId == condominium.Id && period.Label == bounds.Label, cancellationToken);
        if (existing is not null)
            return existing;

        var others = await db.CondominiumPeriods
            .Where(period => period.CondominiumId == condominium.Id && period.Status != CondominiumPeriodStatus.Closed)
            .ToListAsync(cancellationToken);
        if (others.Any(period => FiscalYear.Overlaps(bounds.Start, bounds.End, period.StartDate, period.EndDate)))
            throw new DomainConflictException(CondominiumErrorCodes.PeriodOverlap, "CondominiumPeriodOverlap");

        var now = UtcNow;
        var created = new CondominiumPeriod
        {
            OrgId = condominium.OrgId,
            CondominiumId = condominium.Id,
            Label = bounds.Label,
            StartDate = bounds.Start,
            EndDate = bounds.End,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CondominiumPeriods.Add(created);
        return created;
    }

    private async Task<CondominiumPeriod> EnsureNextPeriodAsync(
        Condominium condominium, CondominiumPeriod period, CancellationToken cancellationToken)
    {
        return await EnsurePeriodAsync(condominium, NextLabel(condominium, period), cancellationToken);
    }

    private async Task RefreshInstallmentStatusAsync(Guid periodId, CancellationToken cancellationToken)
    {
        var installments = await LoadInstallmentsAsync(periodId, cancellationToken);
        var paid = await db.CondominiumPayments
            .Where(payment => payment.CondominiumPeriodId == periodId && payment.CondominiumInstallmentId != null)
            .GroupBy(payment => payment.CondominiumInstallmentId)
            .Select(group => new { Id = group.Key, Amount = group.Sum(payment => payment.Amount) })
            .ToListAsync(cancellationToken);
        var now = UtcNow;
        foreach (var installment in installments)
        {
            if (installment.Status == CondominiumInstallmentStatus.Cancelled)
                continue;
            var amount = paid.FirstOrDefault(row => row.Id == installment.Id)?.Amount ?? 0m;
            installment.Status = amount <= 0.005m
                ? CondominiumInstallmentStatus.Scheduled
                : amount + 0.01m >= installment.TotalAmount
                    ? CondominiumInstallmentStatus.Paid
                    : CondominiumInstallmentStatus.PartiallyPaid;
            installment.UpdatedAt = now;
        }
    }

    private async Task<List<CondominiumInstallment>> LoadInstallmentsAsync(Guid periodId, CancellationToken cancellationToken) =>
        await db.CondominiumInstallments
            .Where(installment => installment.CondominiumPeriodId == periodId && installment.Status != CondominiumInstallmentStatus.Cancelled)
            .OrderBy(installment => installment.SlotIndex)
            .ToListAsync(cancellationToken);

    private async Task<List<CondominiumExpenseLine>> CurrentLinesAsync(
        Guid periodId, CondominiumStatementKind kind, CancellationToken cancellationToken) =>
        await db.CondominiumExpenseLines
            .AsNoTracking()
            .Where(line => line.Statement.CondominiumPeriodId == periodId
                && line.Statement.Kind == kind
                && line.Statement.Status == CondominiumStatementStatus.Confirmed)
            .ToListAsync(cancellationToken);

    private async Task<decimal> CurrentExtraordinaryAsync(Guid periodId, CancellationToken cancellationToken) =>
        await db.CondominiumExpenseLines
            .Where(line => line.Statement.CondominiumPeriodId == periodId
                && line.Statement.Kind == CondominiumStatementKind.Budget
                && line.Statement.Status == CondominiumStatementStatus.Confirmed
                && line.ExpenseNature == CondominiumExpenseNature.Extraordinary)
            .SumAsync(line => (decimal?)line.Amount, cancellationToken) ?? 0m;

    private async Task<Guid?> FirstUnitIdAsync(Guid condominiumId, CancellationToken cancellationToken) =>
        await db.CondominiumUnits
            .Where(unit => unit.CondominiumId == condominiumId)
            .OrderBy(unit => unit.CreatedAt)
            .Select(unit => (Guid?)unit.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A condominium with no property is an org-level row: only an org-wide scope reaches it
    /// (<c>HostScopeQueryExtensions</c> leaves a row with no property outside every restricted scope).
    /// Once a property is linked, the caller must reach that property.
    /// </summary>
    private async Task<Condominium> RequireCondominiumAsync(HostScope scope, Guid condominiumId, CancellationToken cancellationToken)
    {
        var condominium = await db.Condominiums
            .FirstOrDefaultAsync(item => item.Id == condominiumId && item.OrgId == scope.OrgId, cancellationToken)
            ?? throw NotFound();
        if (scope.IsOrgWide)
            return condominium;

        var visible = await db.CondominiumUnits
            .Where(unit => unit.CondominiumId == condominium.Id)
            .Select(unit => unit.Property)
            .InScope(scope)
            .AnyAsync(cancellationToken);
        if (!visible)
            throw NotFound();
        return condominium;
    }

    private async Task<CondominiumPeriod> RequirePeriodAsync(Condominium condominium, Guid periodId, CancellationToken cancellationToken) =>
        await db.CondominiumPeriods
            .FirstOrDefaultAsync(period => period.Id == periodId && period.CondominiumId == condominium.Id, cancellationToken)
        ?? throw new NotFoundException("Period not found")
        {
            Code = CondominiumErrorCodes.PeriodNotFound,
            MessageKey = "CondominiumPeriodNotFound",
        };

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockAsync(Guid condominiumId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.Condominium, condominiumId.ToString("N")));

    private static void EnsureOpen(CondominiumPeriod period)
    {
        if (period.Status == CondominiumPeriodStatus.Closed)
            throw new DomainRuleException(CondominiumErrorCodes.PeriodClosed, "CondominiumPeriodClosed");
    }

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > Condominium.UserIdMaxLength)
            throw new DomainRuleException(CondominiumErrorCodes.StatementInvalid, "CondominiumStatementInvalid");
    }

    private static string RequireText(string? value, int max, string code, string key)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is 0 || text.Length > max)
            throw new DomainRuleException(code, key);
        return text;
    }

    private static string? TrimToNull(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? TrimToLength(string? value, int max)
    {
        var text = TrimToNull(value);
        return text is null ? null : text.Length <= max ? text : text[..max];
    }

    private static bool ValidityOverlaps(DateOnly? from, DateOnly? to, DateOnly? otherFrom, DateOnly? otherTo)
    {
        var start = from ?? DateOnly.MinValue;
        var end = to ?? DateOnly.MaxValue;
        var otherStart = otherFrom ?? DateOnly.MinValue;
        var otherEnd = otherTo ?? DateOnly.MaxValue;
        return start <= otherEnd && otherStart <= end;
    }

    private static bool IsPriorOnly(CondominiumInstallment installment) =>
        Math.Abs(installment.OrdinaryAmount) <= 0.005m
        && Math.Abs(installment.ExtraordinaryAmount) <= 0.005m
        && Math.Abs(installment.PriorSettlementAmount) > 0.005m;

    private static bool IsPdf(StatementFileUpload file) =>
        file.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true
        || (file.Content.Length >= 4 && file.Content[0] == (byte)'%' && file.Content[1] == (byte)'P');

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

    private static NotFoundException NotFound() =>
        new("Condominium not found") { Code = CondominiumErrorCodes.NotFound, MessageKey = "CondominiumNotFound" };

    private static CondominiumPeriodView ToPeriod(CondominiumPeriod period) =>
        new(period.Id, period.Label, period.StartDate, period.EndDate, period.Status);

    private static CondominiumStatementView ToStatement(CondominiumStatement statement) =>
        new(statement.Id, statement.Kind, statement.RevisionNumber, statement.TotalAmount);

    private static CondominiumInstallmentView ToInstallment(CondominiumInstallment installment) =>
        new(
            installment.Id,
            installment.SlotIndex,
            installment.Label,
            installment.DueDate,
            installment.OrdinaryAmount,
            installment.PriorSettlementAmount,
            installment.ExtraordinaryAmount,
            installment.TotalAmount);
}
