using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerFiscalProfileService"/>
/// <remarks>Logs carry ids, codes and field names: never a value of a profile.</remarks>
public sealed partial class OwnerFiscalProfileService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<OwnerFiscalProfileService> logger) : IOwnerFiscalProfileService
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Z]{2}(-[A-Z0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex JurisdictionPattern();

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<OwnerAccountingPolicy> CreatePolicyAsync(Guid orgId, OwnerAccountingPolicyCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var jurisdiction = command.Jurisdiction?.Trim().ToUpperInvariant() ?? string.Empty;
        var ruleCode = command.RuleCode?.Trim() ?? string.Empty;
        var version = command.Version?.Trim() ?? string.Empty;
        var sourceUrl = command.SourceUrl?.Trim() ?? string.Empty;
        var sha = command.SourceSha256?.Trim().ToLowerInvariant() ?? string.Empty;

        RequirePolicyField(JurisdictionPattern().IsMatch(jurisdiction) && jurisdiction.Length <= OwnerAccountingPolicy.JurisdictionMaxLength, "jurisdiction");
        RequirePolicyField(ruleCode.Length is > 0 and <= OwnerAccountingPolicy.RuleCodeMaxLength, "ruleCode");
        RequirePolicyField(version.Length is > 0 and <= OwnerAccountingPolicy.VersionMaxLength, "version");
        RequirePolicyField(
            sourceUrl.Length <= OwnerAccountingPolicy.SourceUrlMaxLength && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps,
            "sourceUrl");
        RequirePolicyField(Sha256Pattern().IsMatch(sha), "sourceSha256");
        RequirePolicyField(command.ValidTo is null || command.ValidTo >= command.ValidFrom, "validTo");
        RequirePolicyField(command.Parameters is not null, "parameters");
        if (command.Parameters!.Problem() is { } field)
            RequirePolicyField(false, field);

        if (await db.OwnerAccountingPolicies.AnyAsync(p => p.OrgId == orgId && p.RuleCode == ruleCode && p.Version == version, ct))
            throw new DomainConflictException(OwnerFiscalErrors.PolicyVersionExists, "OwnerPolicyVersionExists");

        var policy = new OwnerAccountingPolicy
        {
            OrgId = orgId,
            Jurisdiction = jurisdiction,
            RuleCode = ruleCode,
            Version = version,
            ValidFrom = command.ValidFrom,
            ValidTo = command.ValidTo,
            SourceUrl = sourceUrl,
            SourceSha256 = sha,
            ParametersJson = command.Parameters.ToJson(),
            CreatedAt = UtcNow,
            CreatedByUserId = userId,
        };
        db.OwnerAccountingPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner accounting rule {RuleCode} v{Version} of org {OrgId} created", ruleCode, version, orgId);
        return policy;
    }

    public async Task<OwnerAccountingPolicy> ApprovePolicyAsync(Guid orgId, Guid policyId, string approverName, string? userId, CancellationToken ct = default)
    {
        var policy = await db.OwnerAccountingPolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.OrgId == orgId, ct)
            ?? throw new NotFoundException($"Owner accounting policy {policyId} was not found") { Code = "owner_policy_not_found", MessageKey = "OwnerPolicyNotFound" };
        if (policy.ApprovedAt is not null)
            throw new DomainConflictException(OwnerFiscalErrors.PolicyAlreadyApproved, "OwnerPolicyAlreadyApproved");

        var name = approverName?.Trim() ?? string.Empty;
        RequirePolicyField(name.Length is > 0 and <= OwnerAccountingPolicy.ApproverNameMaxLength, "approvedByName");

        policy.ApprovedAt = UtcNow;
        policy.ApprovedByName = name;
        policy.ApprovedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner accounting rule {PolicyId} of org {OrgId} approved", policy.Id, orgId);
        return policy;
    }

    public async Task<IReadOnlyList<OwnerAccountingPolicy>> ListPoliciesAsync(Guid orgId, CancellationToken ct = default) =>
        await db.OwnerAccountingPolicies.AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .OrderBy(p => p.RuleCode)
            .ThenByDescending(p => p.ValidFrom)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<OwnerFiscalProfile> SaveProfileAsync(Guid orgId, Guid ownerId, OwnerFiscalProfileCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RequireOwnerAsync(orgId, ownerId, ct);
        if (command.AccountingPolicyId is { } policyId && !await db.OwnerAccountingPolicies.AnyAsync(p => p.Id == policyId && p.OrgId == orgId, ct))
            throw ProfileInvalid("accountingPolicyId");

        var profile = new OwnerFiscalProfile
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            ValidFrom = command.ValidFrom,
            EntityKind = command.EntityKind,
            LegalName = Text(command.LegalName, OwnerFiscalProfile.LegalNameMaxLength, "legalName"),
            TaxResidenceCountry = Code(command.TaxResidenceCountry, OwnerFiscalProfile.CountryMaxLength, "taxResidenceCountry", exact: true),
            LegalAddress = Text(command.LegalAddress, OwnerFiscalProfile.LegalAddressMaxLength, "legalAddress"),
            FiscalCode = Text(command.FiscalCode, OwnerFiscalProfile.IdentifierMaxLength, "fiscalCode")?.ToUpperInvariant(),
            VatNumber = Text(command.VatNumber, OwnerFiscalProfile.IdentifierMaxLength, "vatNumber")?.ToUpperInvariant(),
            ForeignTaxId = Text(command.ForeignTaxId, OwnerFiscalProfile.IdentifierMaxLength, "foreignTaxId"),
            SettlementCurrency = Code(command.SettlementCurrency, OwnerFiscalProfile.CurrencyMaxLength, "settlementCurrency", exact: true),
            BankAccountReference = Text(command.BankAccountReference, OwnerFiscalProfile.BankAccountMaxLength, "bankAccountReference")?.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
            MandateRole = Text(command.MandateRole, OwnerFiscalProfile.CodeMaxLength, "mandateRole"),
            MandateFrom = command.MandateFrom,
            MandateTo = command.MandateTo,
            RevenueRecognitionRule = Text(command.RevenueRecognitionRule, OwnerFiscalProfile.CodeMaxLength, "revenueRecognitionRule"),
            ExpenseCommissionModel = Text(command.ExpenseCommissionModel, OwnerFiscalProfile.CodeMaxLength, "expenseCommissionModel"),
            WorkingCapitalRule = Text(command.WorkingCapitalRule, OwnerFiscalProfile.CodeMaxLength, "workingCapitalRule"),
            TaxJurisdiction = Code(command.TaxJurisdiction, OwnerFiscalProfile.JurisdictionMaxLength, "taxJurisdiction", exact: false),
            AccountingPolicyId = command.AccountingPolicyId,
            CreatedAt = UtcNow,
            CreatedByUserId = userId,
        };
        if (command.MandateTo is { } mandateTo && command.MandateFrom is { } mandateFrom && mandateTo < mandateFrom)
            throw ProfileInvalid("mandateTo");

        var others = await db.OwnerFiscalProfiles
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId)
            .ToListAsync(ct);
        if (others.Any(p => p.ValidFrom >= command.ValidFrom))
            throw new DomainConflictException(OwnerFiscalErrors.ProfileOverlap, "OwnerFiscalProfileOverlap");

        // The profile in force before the new one ends the day before it starts.
        foreach (var open in others.Where(p => p.ValidTo is null || p.ValidTo >= command.ValidFrom))
            open.ValidTo = command.ValidFrom.AddDays(-1);

        db.OwnerFiscalProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Fiscal profile {ProfileId} of owner {OwnerId} saved from {ValidFrom}", profile.Id, ownerId, profile.ValidFrom);
        return profile;
    }

    public async Task<IReadOnlyList<OwnerFiscalProfile>> ListProfilesAsync(Guid orgId, Guid ownerId, CancellationToken ct = default)
    {
        await RequireOwnerAsync(orgId, ownerId, ct);
        return await db.OwnerFiscalProfiles.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId)
            .OrderByDescending(p => p.ValidFrom)
            .ToListAsync(ct);
    }

    public async Task<OwnerFiscalResolution> ResolveAsync(Guid orgId, Guid ownerId, DateOnly day, CancellationToken ct = default)
    {
        var profile = await db.OwnerFiscalProfiles.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.ValidFrom <= day && (p.ValidTo == null || p.ValidTo >= day))
            .OrderByDescending(p => p.ValidFrom)
            .FirstOrDefaultAsync(ct)
            ?? throw Blocked(OwnerFiscalErrors.ProfileMissing, "OwnerFiscalProfileMissing");

        var missing = MissingFields(profile);
        if (missing.Count > 0)
        {
            throw new DomainRuleException(OwnerFiscalErrors.ProfileIncomplete, "OwnerFiscalProfileIncomplete")
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        if (profile.AccountingPolicyId is not { } policyId)
            throw Blocked(OwnerFiscalErrors.PolicyMissing, "OwnerFiscalPolicyMissing");

        var policy = await db.OwnerAccountingPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId && p.OrgId == orgId, ct)
            ?? throw Blocked(OwnerFiscalErrors.PolicyMissing, "OwnerFiscalPolicyMissing");
        if (policy.ApprovedAt is null)
            throw Blocked(OwnerFiscalErrors.PolicyNotApproved, "OwnerFiscalPolicyNotApproved");
        if (policy.ValidFrom > day || (policy.ValidTo is { } end && end < day))
            throw Blocked(OwnerFiscalErrors.PolicyNotEffective, "OwnerFiscalPolicyNotEffective");
        if (!string.Equals(policy.Jurisdiction, profile.TaxJurisdiction, StringComparison.OrdinalIgnoreCase))
            throw Blocked(OwnerFiscalErrors.PolicyJurisdictionMismatch, "OwnerFiscalPolicyJurisdictionMismatch");

        var parameters = OwnerAccountingRuleParameters.FromJson(policy.ParametersJson);
        if (parameters is null || parameters.Problem() is not null)
            throw Blocked(OwnerFiscalErrors.PolicyNotApproved, "OwnerFiscalPolicyNotApproved");

        return new OwnerFiscalResolution(profile, policy, parameters);
    }

    /// <summary>What a profile still needs to be used: the identity, the residence, the jurisdiction, an identifier, the currency and the recognition rule.</summary>
    internal static IReadOnlyList<string> MissingFields(OwnerFiscalProfile profile)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.LegalName))
            missing.Add("legalName");
        if (string.IsNullOrWhiteSpace(profile.TaxResidenceCountry))
            missing.Add("taxResidenceCountry");
        if (string.IsNullOrWhiteSpace(profile.TaxJurisdiction))
            missing.Add("taxJurisdiction");
        if (string.IsNullOrWhiteSpace(profile.FiscalCode) && string.IsNullOrWhiteSpace(profile.VatNumber) && string.IsNullOrWhiteSpace(profile.ForeignTaxId))
            missing.Add("fiscalCode");
        if (string.IsNullOrWhiteSpace(profile.SettlementCurrency))
            missing.Add("settlementCurrency");
        if (string.IsNullOrWhiteSpace(profile.RevenueRecognitionRule))
            missing.Add("revenueRecognitionRule");
        return missing;
    }

    private async Task RequireOwnerAsync(Guid orgId, Guid ownerId, CancellationToken ct)
    {
        if (!await db.PropertyOwners.AnyAsync(o => o.Id == ownerId && o.OrgId == orgId, ct))
            throw new NotFoundException($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };
    }

    private static string? Text(string? value, int max, string field)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is not null && text.Length > max)
            throw ProfileInvalid(field);
        return text;
    }

    private static string? Code(string? value, int max, string field, bool exact)
    {
        var code = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        if (code is not null && (exact ? code.Length != max || !code.All(char.IsAsciiLetter) : code.Length > max))
            throw ProfileInvalid(field);
        return code;
    }

    private static void RequirePolicyField(bool valid, string field)
    {
        if (!valid)
        {
            throw new DomainRuleException(OwnerFiscalErrors.PolicyInvalid, "OwnerPolicyInvalid")
            {
                Extensions = new Dictionary<string, object?> { ["field"] = field },
            };
        }
    }

    private static DomainRuleException ProfileInvalid(string field) =>
        new(OwnerFiscalErrors.ProfileInvalid, "OwnerFiscalProfileInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    private static DomainRuleException Blocked(string code, string key) => new(code, key);
}
