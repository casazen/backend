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
}

/// <summary>The proposals and the hint of <see cref="IItalianTouristTaxSetup"/>.</summary>
/// <param name="Proposals">
/// «Airbnb incassa, da confermare» for the comuni of the organization's properties that have one (Milano and Firenze only); none for an organization that saved its own list, and none for any other comune.
/// </param>
/// <param name="ChannelDataConflict">
/// True when the record of a recent stay (E25: the collector the host declared, or the one of the calculation it confirmed) contradicts the organization's rule for the channel: tax recorded as collected
/// by the portal while the rule says «at the property», or the opposite. A data-quality hint: no «To do», nothing is changed by it.
/// </param>
public sealed record ItalianTouristTaxSetupView(IReadOnlyList<ItalianPortalProposal> Proposals, bool ChannelDataConflict);

/// <summary>The members the section <c>touristTax</c> of the billing setup carries for an Italian issuer (read only, never saved: the issuer's own answer is <c>platformCollectedTouristTaxChannels</c>).</summary>
public static class ItalianTouristTaxSetupKeys
{
    /// <summary><c>[{ comuneIstat, comune, channel, status }]</c>: the proposals, <c>status</c> being <c>to_confirm</c>.</summary>
    public const string Proposals = "italianPortalProposals";

    /// <summary>A boolean: the records of the stays contradict the organization's rule (the data-quality hint).</summary>
    public const string ChannelDataConflict = "portalTouristTaxConflict";
}
