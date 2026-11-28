using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// The terms of the agency with its owners (E13/F01/US01/T01, NF-110, NF-115): the versioned mandate of each owner (commission, base, flat fees, payment fees),
/// the standard mandate applied in one call to every owner without one, the structured billing address of an owner (encrypted at rest) and the commission
/// rules of the channels of the agency with their configured defaults (⚖ R2).
/// </summary>
/// <remarks>
/// <b>Reach.</b> The web layer decides the permission and passes the caller's <see cref="HostScope"/>; the service applies it: an owner is inside the reach of
/// an org-wide scope when it is an owner of the org, inside a partial one only when every ownership period it holds is on a property the scope reaches (it
/// holds at least one). An owner outside the reach is the same 404 as an owner that does not exist. The channel rules are of the whole org: the controller
/// lets in only an org-wide scope. Logs carry ids and counts, never an address.
/// </remarks>
public interface IOwnerMandateService
{
    Task<IReadOnlyList<OwnerMandate>> ListAsync(HostScope scope, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// A new mandate version of the owner from <see cref="OwnerMandateCommand.ValidFrom"/>: the version in force then ends the day before, the versions never
    /// overlap. 422 <c>owner_mandate_invalid</c> (with the <c>field</c>); 409 <c>owner_mandate_overlap</c> (a version starts on that day or later); 404
    /// <c>owner_not_found</c>.
    /// </summary>
    Task<OwnerMandate> CreateVersionAsync(HostScope scope, Guid ownerId, OwnerMandateCommand command, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The standard mandate (<see cref="OwnerMandateTemplateCommand.Template"/>) for every owner of the org in reach that has an ownership not ended and no
    /// mandate from the first day of the template on (or only the listed owners, with the same conditions): one version each from the template's first day.
    /// The others are skipped. 422 <c>owner_mandate_invalid</c>, <c>owner_mandate_owners_empty</c> (an empty list never means every owner); 404
    /// <c>owner_not_found</c> for a listed owner that is not in reach.
    /// </summary>
    Task<OwnerMandateTemplateResult> ApplyTemplateAsync(HostScope scope, OwnerMandateTemplateCommand command, string? userId, CancellationToken ct = default);

    Task<PropertyOwner> GetAddressAsync(HostScope scope, Guid ownerId, CancellationToken ct = default);

    Task<PropertyOwner> SaveAddressAsync(HostScope scope, Guid ownerId, OwnerAddressCommand command, string? userId, CancellationToken ct = default);

    Task<ChannelCommissionRuleSet> GetChannelRulesAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the channel commission rules of the org with <paramref name="rules"/> (the whole list; an empty list leaves the org with the defaults). A rule
    /// with the channel and the first day of a stored one is that rule and keeps its id. A rule that lines of the owner ledger already name
    /// (<c>channel-rule:{id}</c>) is the evidence of the commission they took: it can be closed (its last day moves) but never removed nor given another percent
    /// or base, 409 <c>channel_commission_rule_in_use</c> (with the <c>ruleId</c>). 422 <c>channel_commission_rules_invalid</c> (with the <c>index</c> and the
    /// <c>field</c>).
    /// </summary>
    Task<ChannelCommissionRuleSet> SaveChannelRulesAsync(Guid orgId, IReadOnlyList<ChannelCommissionRuleCommand>? rules, string? userId, CancellationToken ct = default);
}

public sealed record OwnerMandateCommand
{
    public DateOnly ValidFrom { get; init; }

    public decimal? CommissionPercent { get; init; }

    public CommissionBase? CommissionBase { get; init; }

    /// <summary>Whether the percent and the flat fees include VAT; <c>false</c> by default (⚖ R4: «+ IVA»).</summary>
    public bool CommissionIncludesVat { get; init; }

    public decimal? CleaningFlatRate { get; init; }

    public CleaningRechargeMode? CleaningRechargeMode { get; init; }

    public decimal? LinenFlatRate { get; init; }

    public bool PassthroughPaymentFees { get; init; }

    public CommissionTreatment? WithholdingTreatment { get; init; }

    public CoOwnerTaxReporting? CoOwnerTaxReporting { get; init; }

    public string? DocumentReference { get; init; }
}

public sealed record OwnerMandateTemplateCommand
{
    public OwnerMandateCommand? Template { get; init; }

    public IReadOnlyList<Guid>? OwnerIds { get; init; }
}

public sealed record OwnerMandateTemplateResult(int Created, int Skipped, IReadOnlyList<Guid> CreatedOwnerIds, IReadOnlyList<Guid> SkippedOwnerIds);

public sealed record OwnerAddressCommand
{
    public string? Street { get; init; }

    public string? BuildingNumber { get; init; }

    public string? PostalCode { get; init; }

    public string? Town { get; init; }

    public string? Province { get; init; }

    public string? Country { get; init; }
}

public sealed record ChannelCommissionRuleCommand
{
    public BookingSource Channel { get; init; }

    public decimal? Percent { get; init; }

    public CommissionBase? Base { get; init; }

    public DateOnly ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }
}

public sealed record ChannelCommissionRuleSet(IReadOnlyList<OrgChannelCommissionRule> Rules, IReadOnlyList<ChannelCommissionInForce> Channels);

public sealed record ChannelCommissionInForce(
    BookingSource Channel,
    decimal? Percent,
    CommissionBase? Base,
    bool IsDefault,
    Guid? RuleId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

public static class OwnerMandateErrors
{
    public const string MandateInvalid = "owner_mandate_invalid";
    public const string MandateOverlap = "owner_mandate_overlap";
    public const string TemplateOwnersEmpty = "owner_mandate_owners_empty";
    public const string AddressInvalid = "owner_address_invalid";
    public const string ChannelRulesInvalid = "channel_commission_rules_invalid";
    public const string ChannelRuleInUse = "channel_commission_rule_in_use";
}

public static class OwnerMandateRules
{
    public static bool Covers(OwnerMandate mandate, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        return mandate.ValidFrom <= day && (mandate.ValidTo is not { } last || last >= day);
    }

    /// <summary>
    /// The mandate in force on <paramref name="day"/> for the share of <paramref name="ownerId"/> held through <paramref name="ownershipId"/>: a mandate of that
    /// ownership (the shape of before E13) first, else the owner's own (no ownership); the latest version when more are in force. <c>null</c> without one:
    /// never a commission of zero.
    /// </summary>
    public static OwnerMandate? InForce(IEnumerable<OwnerMandate> mandates, Guid ownerId, Guid? ownershipId, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(mandates);
        var covering = mandates.Where(m => Covers(m, day)).ToList();
        var ofOwnership = ownershipId is { } id
            ? covering.Where(m => m.PropertyOwnershipId == id).MaxBy(m => m.ValidFrom)
            : null;
        return ofOwnership ?? covering.Where(m => m.PropertyOwnershipId is null && m.PropertyOwnerId == ownerId).MaxBy(m => m.ValidFrom);
    }

    public static CleaningRechargeMode CleaningRechargeModeOf(OwnerMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        return mandate.CleaningRechargeMode ?? CleaningRechargeMode.FlatRate;
    }
}

public static class ChannelFeeSources
{
    public const string Payload = "payload";

    public const string RulePrefix = "channel-rule:";

    public const string Default = "channel-default";

    public static string Rule(Guid ruleId) => RulePrefix + ruleId.ToString("D");
}

public sealed record ChannelCommissionTerms(decimal Percent, CommissionBase Base, Guid? RuleId)
{
    public string Source => RuleId is { } id ? ChannelFeeSources.Rule(id) : ChannelFeeSources.Default;
}

public static class ChannelCommissionRules
{
    public static IReadOnlyList<BookingSource> Portals { get; } = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();

    public static OrgChannelCommissionRule? InForce(IEnumerable<OrgChannelCommissionRule> rules, BookingSource channel, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var name = channel.ToString();
        return rules
            .Where(r => string.Equals(r.Channel, name, StringComparison.Ordinal) && r.ValidFrom <= day && (r.ValidTo is not { } last || last >= day))
            .MaxBy(r => r.ValidFrom);
    }

    public static ChannelCommissionTerms? TermsOn(IEnumerable<OrgChannelCommissionRule> rules, OwnerAccountingOptions options, BookingSource channel, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (InForce(rules, channel, day) is { } rule)
            return new ChannelCommissionTerms(rule.Percent, rule.Base, rule.Id);

        return options.TryGetChannelDefault(channel, out var percent)
            ? new ChannelCommissionTerms(percent, CommissionBase.GrossExclTouristTax, null)
            : null;
    }
}
