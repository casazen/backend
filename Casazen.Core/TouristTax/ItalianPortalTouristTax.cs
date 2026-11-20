using Casazen.Core.Entities;

namespace Casazen.Core.TouristTax;

/// <summary>Where the collector of the tourist tax of an Italian stay comes from (<see cref="ItalianPortalTouristTax.Decide"/>), the strongest source first.</summary>
public enum ItalianTouristTaxOrigin
{
    /// <summary>The booking is not of a channel (direct, manual, local): the host collects, nobody has to say so.</summary>
    NotAChannelBooking,

    /// <summary>The active rule of the comune and the channel (platform reference data, added from an official source).</summary>
    ChannelRule,

    /// <summary>The record of the stay's tourist tax (E25): the host's declaration for the stay, or the collector of the calculation the host confirmed.</summary>
    StayRecord,

    /// <summary>
    /// The organization's own answer: the list of the portals that collect it in every comune (the answer it saved, or the default of the configuration while it saved none), or the portal
    /// it confirmed for the comune of the property (<see cref="PortalComune"/>).
    /// </summary>
    Organization,

    /// <summary>Nothing says that the portal collects it: the host collects it at the property (the global default).</summary>
    Default,
}

/// <summary>
/// A portal that collects the tourist tax in one comune, as the organization confirmed it (the proposal of the setup, «Airbnb incassa a Milano», confirmed by a person): the channel and the
/// ISTAT code (six digits) of the comune. It says nothing of any other comune.
/// </summary>
/// <param name="Channel">The portal.</param>
/// <param name="ComuneIstat">The ISTAT code of the comune, six digits.</param>
public sealed record PortalComune(BookingSource Channel, string ComuneIstat);

/// <summary>Who collects the tourist tax of an Italian stay, and where that comes from.</summary>
/// <param name="Collector">Who collects it: never unknown (a stay nobody said anything about is the host's).</param>
/// <param name="Origin">The source that decided it.</param>
public sealed record ItalianTouristTaxDecision(TouristTaxCollector Collector, ItalianTouristTaxOrigin Origin)
{
    /// <summary>True when the host collects it (at the property): the guest document bills it and says «da pagare in struttura» when a portal collected the rest.</summary>
    public bool HostCollects => Collector == TouristTaxCollector.Host;
}

/// <summary>
/// Who collects the tourist tax of an Italian stay, for the document of the guest (FIX/BE-IT-TOURISTTAX-DEFAULTS, decision 2 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R2),
/// pure. <b>The document says «incassata dal portale» only if for that booking it is recorded as collected; otherwise «da pagare in struttura».</b> Why: in the short lettings the subject that
/// collects the rent or intervenes in the payment is responsible for the tax (art. 4 c. 5-ter DL 50/2017), the host in the other cases (art. 4 c. 1-ter D.Lgs. 23/2011); Airbnb collects only where
/// the municipality is registered with it, and no official page says that Booking.com, Expedia or Vrbo collect it in Italy. Writing «incassata» wrongly leaves the host responsible towards the
/// comune; writing «da pagare» wrongly is a double payment that is given back.
/// </summary>
/// <remarks>
/// The order, strongest first: (1) a booking that is not of a channel: the host; (2) the record of the stay and the data of the channel: the active rule of the comune and the channel
/// (<see cref="TouristTaxCollectorRules.Decide"/>), the host's declaration for the stay, the collector of the calculation the host confirmed (E25); (3) the rule of the organization: the portals of
/// its own list, the one it saved or, while it saved none, the default of the configuration (<c>ItalianTouristTax:PlatformCollected</c>, none by default), and the portals it confirmed for one comune
/// (<see cref="PortalComune"/>), which count only for a property in that comune; (4) the global default: the host, at the
/// property. Nothing is ever «unknown»: <see cref="TouristTaxCollectorRules.Decide"/> asks the host who collected, this rule does not wait for the answer.
/// </remarks>
public static class ItalianPortalTouristTax
{
    /// <summary>
    /// Who collects the tourist tax of a stay of <paramref name="source"/>: see the order of <see cref="ItalianPortalTouristTax"/>. <paramref name="channelRule"/> is the active rule of the comune of
    /// the property and the channel, <paramref name="declared"/> the collector the host declared for the stay, <paramref name="confirmed"/> the collector of the calculation the host confirmed, and
    /// <paramref name="organizationChannels"/> the portals the organization says collect it in every comune; <paramref name="organizationComuni"/> the portals it confirmed for one comune, which
    /// count only for a property in that comune (<paramref name="comuneIstat"/>, the ISTAT code of the property): the confirmation of Airbnb for Milano says nothing of a flat in Como.
    /// </summary>
    public static ItalianTouristTaxDecision Decide(
        BookingSource source,
        TouristTaxChannelRule? channelRule,
        TouristTaxCollector? declared,
        TouristTaxCollector? confirmed,
        IReadOnlyCollection<BookingSource> organizationChannels,
        IReadOnlyCollection<PortalComune>? organizationComuni = null,
        string? comuneIstat = null)
    {
        ArgumentNullException.ThrowIfNull(organizationChannels);

        if (!TouristTaxCollectorRules.IsChannelBooking(source))
            return new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.NotAChannelBooking);

        var known = TouristTaxCollectorRules.Decide(source, channelRule, declared);
        if (known.Collector is { } collector)
        {
            var origin = known.Source == TouristTaxCollectorSource.ChannelRule ? ItalianTouristTaxOrigin.ChannelRule : ItalianTouristTaxOrigin.StayRecord;
            return new ItalianTouristTaxDecision(collector, origin);
        }

        if (confirmed is { } confirmedCollector)
            return new ItalianTouristTaxDecision(confirmedCollector, ItalianTouristTaxOrigin.StayRecord);

        return OrganizationCollects(source, organizationChannels, organizationComuni, comuneIstat)
            ? new ItalianTouristTaxDecision(TouristTaxCollector.Channel, ItalianTouristTaxOrigin.Organization)
            : new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.Default);
    }

    /// <summary>
    /// Whether the organization says the portal collects the tax of a stay of <paramref name="source"/> in the comune <paramref name="comuneIstat"/>: the channel is in its list of the portals that collect
    /// it everywhere, or it confirmed that very channel for that very comune. A comune it did not confirm (or a property with no ISTAT code) is never taken from another comune's confirmation.
    /// </summary>
    public static bool OrganizationCollects(
        BookingSource source,
        IReadOnlyCollection<BookingSource> organizationChannels,
        IReadOnlyCollection<PortalComune>? organizationComuni,
        string? comuneIstat)
    {
        ArgumentNullException.ThrowIfNull(organizationChannels);

        if (organizationChannels.Contains(source))
            return true;

        var comune = comuneIstat?.Trim();
        return organizationComuni is { Count: > 0 } && !string.IsNullOrEmpty(comune) && organizationComuni.Any(c => c.Channel == source && string.Equals(c.ComuneIstat, comune, StringComparison.Ordinal));
    }

    /// <summary>
    /// What the organization's own rule says of a channel in a comune: the portal collects it when the channel is in its list (everywhere) or it confirmed the channel for that comune, the host does
    /// otherwise.
    /// </summary>
    public static TouristTaxCollector OrganizationRuleOf(
        BookingSource source,
        IReadOnlyCollection<BookingSource> organizationChannels,
        IReadOnlyCollection<PortalComune>? organizationComuni = null,
        string? comuneIstat = null) =>
        OrganizationCollects(source, organizationChannels, organizationComuni, comuneIstat) ? TouristTaxCollector.Channel : TouristTaxCollector.Host;

    /// <summary>
    /// Whether the collector the record of a stay gives (<paramref name="recorded"/>: the host's declaration, else the collector of the calculation it confirmed) contradicts the organization's rule
    /// for the channel: a record of the tax collected by the portal while the rule says «at the property», or the opposite. Nothing recorded, or a booking that is not of a channel, contradicts nothing.
    /// </summary>
    public static bool Contradicts(
        BookingSource source,
        TouristTaxCollector? recorded,
        IReadOnlyCollection<BookingSource> organizationChannels,
        IReadOnlyCollection<PortalComune>? organizationComuni = null,
        string? comuneIstat = null)
    {
        ArgumentNullException.ThrowIfNull(organizationChannels);

        return TouristTaxCollectorRules.IsChannelBooking(source)
               && recorded is { } collector
               && collector != OrganizationRuleOf(source, organizationChannels, organizationComuni, comuneIstat);
    }
}

/// <summary>A suggestion of the setup: the portal collects the tourist tax in a comune, for the host to confirm (nothing is ever saved or applied by it).</summary>
/// <param name="ComuneIstat">The ISTAT code of the comune (six digits).</param>
/// <param name="Comune">The name of the comune.</param>
/// <param name="Channel">The name of the <see cref="BookingSource"/> the suggestion is about.</param>
/// <param name="Status"><see cref="ItalianPortalProposals.ToConfirm"/>: a person has to confirm it; <see cref="ItalianPortalProposals.ToVerify"/>: not verified yet.</param>
public sealed record ItalianPortalProposal(string ComuneIstat, string Comune, string Channel, string Status);

/// <summary>
/// The comuni where the setup proposes «Airbnb incassa, da confermare» (FIX/BE-IT-TOURISTTAX-DEFAULTS, research R2): <b>only Milano (ISTAT 015146) and Firenze (ISTAT 048017)</b>, the two comuni
/// whose own pages were read on 2026-11-20 (Comune di Milano, «Airbnb: gestione imposta di soggiorno», 2026-05-05; Comune di Firenze, FAQ 2023). Every other comune, Roma included (its agreement
/// with Airbnb was not read), has none. A proposal is a suggestion: it is read, never saved, never applied to a document, and the host confirms it by saving its own list of portals.
/// </summary>
public static class ItalianPortalProposals
{
    /// <summary>The proposal waits for a person: «Airbnb incassa, da confermare».</summary>
    public const string ToConfirm = "to_confirm";

    /// <summary>The comune is known to have an agreement with the portal, but nobody has read it (not proposed yet).</summary>
    public const string ToVerify = "to_verify";

    /// <summary>The ISTAT code of Milano.</summary>
    public const string MilanoIstat = "015146";

    /// <summary>The ISTAT code of Firenze.</summary>
    public const string FirenzeIstat = "048017";

    private static readonly ItalianPortalProposal[] Catalog =
    [
        new(MilanoIstat, "Milano", nameof(BookingSource.Airbnb), ToConfirm),
        new(FirenzeIstat, "Firenze", nameof(BookingSource.Airbnb), ToConfirm),
    ];

    /// <summary>The proposals for the comuni named (ISTAT codes; blank ones are skipped): the ones of the catalog, in its order, and nothing for any other comune.</summary>
    public static IReadOnlyList<ItalianPortalProposal> For(IEnumerable<string?> comuneIstatCodes)
    {
        ArgumentNullException.ThrowIfNull(comuneIstatCodes);

        var codes = comuneIstatCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .ToHashSet(StringComparer.Ordinal);
        return [.. Catalog.Where(proposal => codes.Contains(proposal.ComuneIstat))];
    }
}
