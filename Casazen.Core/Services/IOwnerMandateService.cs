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
    /// <summary>The mandate versions of the owner, the newest first. 404 <c>owner_not_found</c>.</summary>
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

    /// <summary>The owner with its billing address, decrypted. 404 <c>owner_not_found</c>.</summary>
    Task<PropertyOwner> GetAddressAsync(HostScope scope, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the structured billing address of the owner (street, building number, postal code, town, province, country ISO-2), stored encrypted. 422
    /// <c>owner_address_invalid</c> (with the <c>field</c>); 404 <c>owner_not_found</c>.
    /// </summary>
    Task<PropertyOwner> SaveAddressAsync(HostScope scope, Guid ownerId, OwnerAddressCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The channel commission rules of the org and, for each portal, what applies today: the rule in force, else the configured default (marked).</summary>
    Task<ChannelCommissionRuleSet> GetChannelRulesAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the channel commission rules of the org with <paramref name="rules"/> (the whole list; an empty list leaves the org with the defaults). A rule
    /// identical to a stored one keeps its id. 422 <c>channel_commission_rules_invalid</c> (with the <c>index</c> and the <c>field</c>).
    /// </summary>
    Task<ChannelCommissionRuleSet> SaveChannelRulesAsync(Guid orgId, IReadOnlyList<ChannelCommissionRuleCommand>? rules, string? userId, CancellationToken ct = default);
}

/// <summary>A mandate version. The enums left out take their defaults: the base of the gross without the tourist tax, the commission deductible.</summary>
public sealed record OwnerMandateCommand
{
    /// <summary>The first day of the version (Europe/Rome). Required.</summary>
    public DateOnly ValidFrom { get; init; }

    /// <summary>The commission of the agency, 0 to 100 with at most two decimals.</summary>
    public decimal CommissionPercent { get; init; }

    /// <summary>What the commission is a percentage of; <c>null</c> = <see cref="CommissionBase.GrossExclTouristTax"/> (⚖ R3).</summary>
    public CommissionBase? CommissionBase { get; init; }

    /// <summary>Whether the percent and the flat fees include VAT; <c>false</c> by default (⚖ R4: «+ IVA»).</summary>
    public bool CommissionIncludesVat { get; init; }

    /// <summary>The flat fee of the cleaning of a stay, in the settlement currency of the owner; <c>null</c> = none.</summary>
    public decimal? CleaningFlatRate { get; init; }

    /// <summary>The flat fee of the linen of a stay, in the settlement currency of the owner; <c>null</c> = none.</summary>
    public decimal? LinenFlatRate { get; init; }

    /// <summary>Whether the fees of the card payments collected through CasaZen pass to the owner.</summary>
    public bool PassthroughPaymentFees { get; init; }

    /// <summary>The tax treatment of the commission; <c>null</c> = <see cref="CommissionTreatment.Deductible"/>.</summary>
    public CommissionTreatment? WithholdingTreatment { get; init; }

    /// <summary>Where the signed mandate is (a storage key or an https address), optional: without it the readiness says <c>mandate_document_missing</c>.</summary>
    public string? DocumentReference { get; init; }
}

/// <summary>The standard mandate and, optionally, the owners it is for.</summary>
public sealed record OwnerMandateTemplateCommand
{
    /// <summary>The mandate every owner without one gets, from its <see cref="OwnerMandateCommand.ValidFrom"/>.</summary>
    public OwnerMandateCommand? Template { get; init; }

    /// <summary>Only these owners; <c>null</c> = every owner of the org in reach. An empty list is refused: it never means every owner.</summary>
    public IReadOnlyList<Guid>? OwnerIds { get; init; }
}

/// <summary>What the standard mandate did: the owners that got a version and the ones skipped (no ownership in force or to come, or a mandate already).</summary>
public sealed record OwnerMandateTemplateResult(int Created, int Skipped, IReadOnlyList<Guid> CreatedOwnerIds, IReadOnlyList<Guid> SkippedOwnerIds);

/// <summary>The structured billing address of an owner (ISO 20022 shape: street, building number, postal code, town, country).</summary>
public sealed record OwnerAddressCommand
{
    public string? Street { get; init; }

    public string? BuildingNumber { get; init; }

    public string? PostalCode { get; init; }

    public string? Town { get; init; }

    /// <summary>The province (the two letters in Italy, required there) or the canton or region abroad, optional.</summary>
    public string? Province { get; init; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string? Country { get; init; }
}

/// <summary>A commission rule of a channel, as the whole list of a <c>PUT</c> carries it.</summary>
public sealed record ChannelCommissionRuleCommand
{
    /// <summary>The portal (<see cref="FiscalCopy.IsOtaBookingSource"/>).</summary>
    public BookingSource Channel { get; init; }

    /// <summary>0 to 100 with at most two decimals.</summary>
    public decimal Percent { get; init; }

    /// <summary>What the percent is of; <c>null</c> = <see cref="CommissionBase.GrossExclTouristTax"/>.</summary>
    public CommissionBase? Base { get; init; }

    /// <summary>The first check-out day the rule applies to. Required.</summary>
    public DateOnly ValidFrom { get; init; }

    /// <summary>The last check-out day (included); <c>null</c> = no end.</summary>
    public DateOnly? ValidTo { get; init; }
}

/// <summary>The rules of the org and what applies today to each portal.</summary>
public sealed record ChannelCommissionRuleSet(IReadOnlyList<OrgChannelCommissionRule> Rules, IReadOnlyList<ChannelCommissionInForce> Channels);

/// <summary>
/// What applies today to the stays of a portal: the rule of the org in force (<see cref="RuleId"/>), or the configured default (<see cref="IsDefault"/>), or
/// nothing (<see cref="Percent"/> <c>null</c>: the stays of the channel without a commission in the reservation are held back by the readiness).
/// </summary>
public sealed record ChannelCommissionInForce(
    BookingSource Channel,
    decimal? Percent,
    CommissionBase? Base,
    bool IsDefault,
    Guid? RuleId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

/// <summary>The stable codes of the mandates, the owner address and the channel rules.</summary>
public static class OwnerMandateErrors
{
    public const string MandateInvalid = "owner_mandate_invalid";
    public const string MandateOverlap = "owner_mandate_overlap";
    public const string TemplateOwnersEmpty = "owner_mandate_owners_empty";
    public const string AddressInvalid = "owner_address_invalid";
    public const string ChannelRulesInvalid = "channel_commission_rules_invalid";
}

/// <summary>Which mandate applies to an owner's share on a day: the one definition of the readiness, the owner ledger and the statements.</summary>
public static class OwnerMandateRules
{
    /// <summary>True when the version is in force on <paramref name="day"/> (its first and last day included).</summary>
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
}

/// <summary>Where the commission of a channel on a stay comes from: written in the <see cref="OwnerLedgerEntry.RuleVersion"/> of its ledger line.</summary>
public static class ChannelFeeSources
{
    /// <summary>The commission the reservation brought (<see cref="Booking.OtaCommission"/>).</summary>
    public const string Payload = "payload";

    /// <summary>The prefix of a rule of the org: <c>channel-rule:{id}</c>.</summary>
    public const string RulePrefix = "channel-rule:";

    /// <summary>The configured default of the channel (<c>OwnerAccounting:ChannelCommissionDefaults</c>).</summary>
    public const string Default = "channel-default";

    /// <summary><c>channel-rule:{id}</c>, the id with its dashes.</summary>
    public static string Rule(Guid ruleId) => RulePrefix + ruleId.ToString("D");
}

/// <summary>The commission terms of a channel on a day: a percent of a base, and where they come from.</summary>
public sealed record ChannelCommissionTerms(decimal Percent, CommissionBase Base, Guid? RuleId)
{
    /// <summary><see cref="ChannelFeeSources.Rule"/> of the rule, or <see cref="ChannelFeeSources.Default"/>.</summary>
    public string Source => RuleId is { } id ? ChannelFeeSources.Rule(id) : ChannelFeeSources.Default;
}

/// <summary>Which commission of a channel applies to a stay without one in its reservation (⚖ R2): the one definition of the ledger and the screens.</summary>
public static class ChannelCommissionRules
{
    /// <summary>The portals a rule can be written for: the channels of <see cref="FiscalCopy.IsOtaBookingSource"/>, in the order of the enum.</summary>
    public static IReadOnlyList<BookingSource> Portals { get; } = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();

    /// <summary>The rule of the org for <paramref name="channel"/> in force on <paramref name="day"/> (the latest first day when more are), or <c>null</c>.</summary>
    public static OrgChannelCommissionRule? InForce(IEnumerable<OrgChannelCommissionRule> rules, BookingSource channel, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var name = channel.ToString();
        return rules
            .Where(r => string.Equals(r.Channel, name, StringComparison.Ordinal) && r.ValidFrom <= day && (r.ValidTo is not { } last || last >= day))
            .MaxBy(r => r.ValidFrom);
    }

    /// <summary>
    /// The terms for a stay of <paramref name="channel"/> that checks out on <paramref name="day"/>: the rule of the org in force, else the configured default
    /// on the gross without the tourist tax; <c>null</c> when there is neither (never a commission of zero by guess).
    /// </summary>
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
