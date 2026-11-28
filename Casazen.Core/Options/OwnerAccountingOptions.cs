using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>OwnerAccounting</c> (E13/F01/US01/T01, E18/F01/US01/T01): the settings of the owner ledger that are prudent defaults to be confirmed by an
/// accountant (⚖): <see cref="ChannelCommissionDefaults"/> and the Italian short-let withholding rule seeded as a draft, <see cref="ItalianShortLetRule"/>. Nothing
/// here is validated at startup: a value that is not a portal or not a percent is ignored, so the stays of that channel are held back by the readiness
/// (<c>channel_commission_missing</c>) instead of being posted with a guessed commission; an incomplete rule seeds no draft. Runbook
/// <c>docs/runbooks/owner-accounting.md</c>, <c>docs/runbooks/deploy-checklist.md</c> § 2.11.
/// </summary>
public class OwnerAccountingOptions
{
    public const string SectionName = "OwnerAccounting";

    /// <summary>
    /// The Italian short-let withholding rule (⚖ I9 and R5, E18/F01/US01/T01): seeded at startup as a rule set of every agency with owners that is <b>not
    /// approved</b>, so it is never used until the accountant approves it (<c>IOwnerFiscalProfileService.EnsureItalianShortLetDraftAsync</c>). Its
    /// <see cref="OwnerWithholdingRuleTemplate.PortalsWithholding"/> is also what a rule set that does not say takes. Committed in <c>appsettings.json</c>
    /// (<c>OwnerAccounting:ItalianShortLetRule:*</c>), never written in the code.
    /// </summary>
    public OwnerWithholdingRuleTemplate ItalianShortLetRule { get; set; } = new();

    /// <summary>
    /// The commission a portal keeps, in percent of the gross of the stay without the tourist tax, used for a stay whose reservation brings no
    /// commission when the agency has no rule of its own for the channel in force at the check-out (⚖ R2). The key is the name of the
    /// <see cref="BookingSource"/> (<c>OwnerAccounting:ChannelCommissionDefaults:BookingCom</c>, env var
    /// <c>OwnerAccounting__ChannelCommissionDefaults__BookingCom</c>). Committed in <c>appsettings.json</c>: BookingCom 15, Expedia 18, Airbnb 15.5 (host-only
    /// fee), Vrbo 8; never written in the code.
    /// </summary>
    public Dictionary<string, decimal> ChannelCommissionDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetChannelDefault(BookingSource channel, out decimal percent)
    {
        percent = 0m;
        if (!FiscalCopy.IsOtaBookingSource(channel))
            return false;

        foreach (var (key, value) in ChannelCommissionDefaults)
        {
            if (EnumNames.TryParseDefined<BookingSource>(key, out var named) && named == channel && value is >= 0m and <= 100m)
            {
                percent = value;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A withholding rule set as the configuration gives it (<see cref="OwnerAccountingOptions.ItalianShortLetRule"/>, ⚖ I9 and R5): what the seeded draft is made of.
/// Every figure is the configuration's (the rate, the tax code of the F24, the components of the base, the rounding); none has a default in the code.
/// </summary>
public class OwnerWithholdingRuleTemplate
{
    public string? Jurisdiction { get; set; }

    public string? RuleCode { get; set; }

    public string? Version { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public string? SourceUrl { get; set; }

    public string? SourceSha256 { get; set; }

    public decimal? WithholdingRatePercent { get; set; }

    public string? WithholdingTaxCode { get; set; }

    public List<string> GrossBaseComponents { get; set; } = [];

    public string? Rounding { get; set; }

    public int? RetentionYears { get; set; }

    public List<string> PortalsWithholding { get; set; } = [];

    public OwnerAccountingRuleParameters ToParameters() => new()
    {
        WithholdingApplies = true,
        WithholdingRatePercent = WithholdingRatePercent,
        WithholdingTaxCode = WithholdingTaxCode?.Trim(),
        RequiresAgencyCollection = true,
        RequiresQualifyingShortLet = true,
        GrossBaseComponents = [.. GrossBaseComponents.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())],
        Rounding = Rounding?.Trim(),
        RetentionYears = RetentionYears,
        PortalsWithholding = [.. PortalsWithholding.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())],
    };
}
