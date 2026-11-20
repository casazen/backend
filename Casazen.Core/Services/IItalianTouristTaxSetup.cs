using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// What the billing setup (<see cref="IBillingSetupService"/>) tells an Italian organization about the tourist tax of the portal bookings, besides its own answer (FIX/BE-IT-TOURISTTAX-DEFAULTS,
/// decision 2 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R2): the proposals for the comuni of its properties, and a hint when the records of its stays contradict its rule.
/// Read only: nothing is saved by reading it, and nothing it says is applied to a document until the host confirms its own list of portals.
/// </summary>
public interface IItalianTouristTaxSetup
{
    /// <summary>
    /// The proposals and the hint for <paramref name="orgId"/>. <paramref name="organizationChannels"/> is the organization's rule as the guest documents read it
    /// (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>); <paramref name="organizationConfirmed"/> says the organization saved its own list, which then stands and is
    /// never proposed over.
    /// </summary>
    Task<ItalianTouristTaxSetupView> ReadAsync(
        Guid orgId,
        IReadOnlyList<BookingSource> organizationChannels,
        bool organizationConfirmed,
        CancellationToken ct = default);

    /// <summary>
    /// The portals the organization confirms for one comune each (the proposal «Airbnb incassa a Milano», confirmed by a person): the whole list, which replaces the one it had; an empty one takes every
    /// confirmation away (it never means «every comune»). They count only for a property in that comune and never touch the list of the portals that collect it everywhere. Only for an Italian issuer
    /// that already has its profile; 422 <c>issuer_field_invalid</c> on <c>portalTouristTaxComuni</c> for a channel that is not a portal, an ISTAT code that is not six digits, or a profile that is not
    /// Italian; 422 <c>issuer_profile_incomplete</c> while the profile does not exist. The caller must reach the whole organization (the setup is the organization's): the controller checks it.
    /// </summary>
    Task ConfirmComuniAsync(Guid orgId, IReadOnlyList<PortalComune> comuni, string? updatedByUserId, CancellationToken ct = default);
}

/// <summary>The proposals and the hint of <see cref="IItalianTouristTaxSetup"/>.</summary>
/// <param name="Proposals">
/// «Airbnb incassa, da confermare» for the comuni of the organization's properties that have one (Milano and Firenze only); none for an organization that saved its own list, and none for any other comune.
/// </param>
/// <param name="ChannelDataConflict">
/// True when the record of a recent stay (E25: the collector the host declared, or the one of the calculation it confirmed) contradicts the organization's rule for the channel: tax recorded as collected
/// by the portal while the rule says «at the property», or the opposite. A data-quality hint: no «To do», nothing is changed by it.
/// </param>
/// <param name="ConfirmedComuni">The portals the organization confirmed for one comune each (the proposals a person confirmed): they count only for a property in that comune.</param>
public sealed record ItalianTouristTaxSetupView(IReadOnlyList<ItalianPortalProposal> Proposals, bool ChannelDataConflict, IReadOnlyList<ItalianPortalConfirmation> ConfirmedComuni);

/// <summary>A portal the organization confirmed for one comune: the ISTAT code (six digits) and the name of the <see cref="BookingSource"/>.</summary>
public sealed record ItalianPortalConfirmation(string ComuneIstat, string Channel);

/// <summary>The members the section <c>touristTax</c> of the billing setup carries for an Italian issuer (read only, never saved: the issuer's own answer is <c>platformCollectedTouristTaxChannels</c>).</summary>
public static class ItalianTouristTaxSetupKeys
{
    /// <summary><c>[{ comuneIstat, comune, channel, status }]</c>: the proposals, <c>status</c> being <c>to_confirm</c>.</summary>
    public const string Proposals = "italianPortalProposals";

    /// <summary>A boolean: the records of the stays contradict the organization's rule (the data-quality hint).</summary>
    public const string ChannelDataConflict = "portalTouristTaxConflict";

    /// <summary><c>[{ comuneIstat, channel }]</c>: the portals the organization confirmed for one comune each (the proposals a person confirmed), saved by <c>PUT api/billing-setup/touristTax/portal-comuni</c>.</summary>
    public const string ConfirmedComuni = "confirmedPortalTouristTaxComuni";
}
