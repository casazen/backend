using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The fiscal and accounting policy of the owners of an agency (NF-109, NF-110, ADR-012, ADR-019, E06/F05/US02/T01): the versioned rule sets the qualified
/// accountant approves, the effective-dated fiscal profile of each owner, and the one question every statement, payout and tax decision asks first:
/// «which profile and which approved rule apply to this owner on this day?».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No default.</b> No rate, no applicability and no jurisdiction is inferred in the code (no 22% VAT, no 21% withholding, no «Italian because the
/// country is IT»): the rule set says them, and a profile without an approved rule set in force blocks with a precise code.</item>
/// <item><b>Tenant.</b> The service names the org of every row it reads or writes; the web layer decides who may see the full values.</item>
/// </list>
/// </remarks>
public interface IOwnerFiscalProfileService
{
    /// <summary>A new rule set (not approved yet). 422 <c>owner_policy_invalid</c> (with the <c>field</c>); 409 <c>owner_policy_version_exists</c>.</summary>
    Task<OwnerAccountingPolicy> CreatePolicyAsync(Guid orgId, OwnerAccountingPolicyCommand command, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Records the qualified accountant's approval of a rule set (once: an approved rule set never changes; 409 <c>owner_policy_already_approved</c>).
    /// 422 <c>owner_policy_invalid</c> without the approver's name.
    /// </summary>
    Task<OwnerAccountingPolicy> ApprovePolicyAsync(Guid orgId, Guid policyId, string approverName, string? userId, CancellationToken ct = default);

    /// <summary>The rule sets of the org, the newest first.</summary>
    Task<IReadOnlyList<OwnerAccountingPolicy>> ListPoliciesAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// A new profile of the owner from <see cref="OwnerFiscalProfileCommand.ValidFrom"/>: the profile in force before it ends the day before; a profile that
    /// would overlap a later one is 409 <c>owner_fiscal_profile_overlap</c>. 404 for an owner of another org.
    /// </summary>
    Task<OwnerFiscalProfile> SaveProfileAsync(Guid orgId, Guid ownerId, OwnerFiscalProfileCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The profiles of the owner, the newest first; 404 for an owner of another org.</summary>
    Task<IReadOnlyList<OwnerFiscalProfile>> ListProfilesAsync(Guid orgId, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// The one complete profile in force on <paramref name="day"/> and its approved rule set in force on that day, or a precise blocking error (422):
    /// <c>owner_fiscal_profile_missing</c>, <c>owner_fiscal_profile_incomplete</c> (with the <c>fields</c>), <c>owner_fiscal_policy_missing</c>,
    /// <c>owner_fiscal_policy_not_approved</c>, <c>owner_fiscal_policy_not_effective</c>, <c>owner_fiscal_policy_jurisdiction_mismatch</c>.
    /// </summary>
    Task<OwnerFiscalResolution> ResolveAsync(Guid orgId, Guid ownerId, DateOnly day, CancellationToken ct = default);
}

/// <summary>The profile and the approved rule set that apply to an owner on a day, with the parameters of the rule.</summary>
public sealed record OwnerFiscalResolution(OwnerFiscalProfile Profile, OwnerAccountingPolicy Policy, OwnerAccountingRuleParameters Parameters);

/// <summary>A new rule set.</summary>
public sealed record OwnerAccountingPolicyCommand
{
    public string? Jurisdiction { get; init; }
    public string? RuleCode { get; init; }
    public string? Version { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceSha256 { get; init; }
    public OwnerAccountingRuleParameters? Parameters { get; init; }
}

/// <summary>A new profile of an owner.</summary>
public sealed record OwnerFiscalProfileCommand
{
    public DateOnly ValidFrom { get; init; }
    public OwnerEntityKind EntityKind { get; init; } = OwnerEntityKind.NaturalPerson;
    public string? LegalName { get; init; }
    public string? TaxResidenceCountry { get; init; }
    public string? LegalAddress { get; init; }
    public string? FiscalCode { get; init; }
    public string? VatNumber { get; init; }
    public string? ForeignTaxId { get; init; }
    public string? SettlementCurrency { get; init; }
    public string? BankAccountReference { get; init; }
    public string? MandateRole { get; init; }
    public DateOnly? MandateFrom { get; init; }
    public DateOnly? MandateTo { get; init; }
    public string? RevenueRecognitionRule { get; init; }
    public string? ExpenseCommissionModel { get; init; }
    public string? WorkingCapitalRule { get; init; }
    public string? TaxJurisdiction { get; init; }
    public Guid? AccountingPolicyId { get; init; }
}

/// <summary>
/// The parameters of a rule set (<see cref="OwnerAccountingPolicy.ParametersJson"/>): whether a withholding applies at the payment to the owner and how,
/// which components of the gross form its base, the tax code of the F24, the rounding and the retention. Every figure is the approved one: nothing here has
/// a default rate.
/// </summary>
public sealed record OwnerAccountingRuleParameters
{
    /// <summary>The rule asks a withholding at the payment to the beneficiary (an Italian short-let rule); <c>false</c> for a Swiss or any other rule.</summary>
    public bool WithholdingApplies { get; init; }

    /// <summary>The rate of the withholding in percent; required when <see cref="WithholdingApplies"/>.</summary>
    public decimal? WithholdingRatePercent { get; init; }

    /// <summary>The tax code of the F24 (<c>1919</c> only while the approved rule says so).</summary>
    public string? WithholdingTaxCode { get; init; }

    /// <summary>The applicability needs that the agency collected the rent or intervened in its payment.</summary>
    public bool RequiresAgencyCollection { get; init; } = true;

    /// <summary>The applicability needs a qualifying short-let contract.</summary>
    public bool RequiresQualifyingShortLet { get; init; } = true;

    /// <summary>The components of the gross that form the base (<c>rent</c>, <c>cleaning</c>, <c>linen</c>, <c>commission</c>...).</summary>
    public IReadOnlyList<string> GrossBaseComponents { get; init; } = [];

    /// <summary>The rounding of the withholding, to the cent: <c>half_up</c> or <c>half_even</c>, as the approved rule says (no default).</summary>
    public string? Rounding { get; init; }

    /// <summary>The years the evidence is kept (Italy at least ten, Swiss immovable-property evidence up to twenty).</summary>
    public int? RetentionYears { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The parameters of <paramref name="json"/>; <c>null</c> when they cannot be read.</summary>
    public static OwnerAccountingRuleParameters? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OwnerAccountingRuleParameters>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The field that makes the parameters unusable, or <c>null</c> when they are complete.</summary>
    public string? Problem() =>
        WithholdingApplies switch
        {
            true when WithholdingRatePercent is not (> 0m and <= 100m) => "withholdingRatePercent",
            true when string.IsNullOrWhiteSpace(WithholdingTaxCode) => "withholdingTaxCode",
            true when GrossBaseComponents.Count == 0 => "grossBaseComponents",
            _ when Rounding is not ("half_up" or "half_even") => "rounding",
            _ when RetentionYears is not (> 0 and <= 50) => "retentionYears",
            _ => null,
        };
}

/// <summary>The stable codes of the errors of the owner fiscal policy, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OwnerFiscalErrors
{
    public const string PolicyInvalid = "owner_policy_invalid";
    public const string PolicyVersionExists = "owner_policy_version_exists";
    public const string PolicyAlreadyApproved = "owner_policy_already_approved";
    public const string ProfileOverlap = "owner_fiscal_profile_overlap";
    public const string ProfileInvalid = "owner_fiscal_profile_invalid";
    public const string ProfileMissing = "owner_fiscal_profile_missing";
    public const string ProfileIncomplete = "owner_fiscal_profile_incomplete";
    public const string PolicyMissing = "owner_fiscal_policy_missing";
    public const string PolicyNotApproved = "owner_fiscal_policy_not_approved";
    public const string PolicyNotEffective = "owner_fiscal_policy_not_effective";
    public const string PolicyJurisdictionMismatch = "owner_fiscal_policy_jurisdiction_mismatch";
    public const string OwnerNotFound = "owner_not_found";
}

/// <summary>How the full values of a fiscal profile are shown to a caller without <c>agency.owner.financial.manage</c>.</summary>
public static class OwnerFiscalMasking
{
    /// <summary>The last four characters of <paramref name="value"/>, the rest as <c>•</c>; <c>null</c> stays <c>null</c>.</summary>
    public static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 4 ? new string('•', value.Length) : new string('•', value.Length - 4) + value[^4..];
}
