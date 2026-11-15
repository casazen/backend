using Casazen.Core.Entities;
using Casazen.Core.Validation;

namespace Casazen.Core.Bookings;

/// <summary>
/// What a guest's booking says about where the guest came from (E27/F01/US01/T01, NF-123): four marketing values kept on the booking, the
/// <c>source</c>, the <c>medium</c> and the <c>campaign</c> of the link the guest booked from (<c>utm_source=agency-site&amp;utm_medium=link&amp;utm_campaign=...</c>)
/// and the host name of the site the guest came from. Never personal data: every value passes <see cref="Sanitize"/> before it is stored.
/// </summary>
/// <remarks>
/// The attribution decides one thing, with the source of the booking (<see cref="BookingOrigins.Of(BookingSource, string?)"/>): whether a
/// booking of the booking site came from the site of the agency (<see cref="AgencySiteSource"/>, <see cref="OctorateBookingEngineSource"/>) or from
/// CasaZen's own site. It never changes <see cref="BookingSource"/>: the invoicing reasons per channel.
/// </remarks>
public static class BookingAttribution
{
    /// <summary>The longest <c>source</c> kept: the column is <c>varchar(40)</c>.</summary>
    public const int MaxSourceLength = 40;

    /// <summary>The longest <c>medium</c> kept: the column is <c>varchar(40)</c>.</summary>
    public const int MaxMediumLength = 40;

    /// <summary>The longest <c>campaign</c> kept: the column is <c>varchar(80)</c>.</summary>
    public const int MaxCampaignLength = 80;

    /// <summary>The longest referrer host kept: the column is <c>varchar(255)</c> (a host name has at most <see cref="SignupAttributionRules.MaxReferrerHostLength"/>).</summary>
    public const int MaxReferrerHostLength = 255;

    /// <summary>The source of a booking made from a link the agency copied from CasaZen (the link generator sets <c>utm_source=agency-site</c>).</summary>
    public const string AgencySiteSource = "agency-site";

    /// <summary>The source of a reservation of the booking engine of the agency on the channel manager, as the import records it (it stays <see cref="BookingSource.Direct"/>).</summary>
    public const string OctorateBookingEngineSource = "octorate-booking-engine";

    /// <summary>The sources that say a booking of the booking site came from the site of the agency: the one definition (the list filter reads it too).</summary>
    public static IReadOnlyList<string> AgencySources { get; } = [AgencySiteSource, OctorateBookingEngineSource];

    /// <summary>True when <paramref name="source"/> (as stored, lower case) is one of <see cref="AgencySources"/>.</summary>
    public static bool IsAgencySource(string? source) =>
        source is not null && AgencySources.Contains(source, StringComparer.Ordinal);

    /// <summary>The word a reservation of the booking engine of the agency goes by in a file a guest or a host reads (<see cref="SourceForExport"/>).</summary>
    public const string AgencyBookingEngineExportName = "agency-booking-engine";

    /// <summary>
    /// The source of a booking as the export of the guest's data says it (art. 15, E27/F01/US01/T01): as it is stored, but for the token of the booking engine of
    /// the agency (<see cref="OctorateBookingEngineSource"/>), which names the supplier of the channel manager and so never leaves in a file for a guest or a host:
    /// it goes as <see cref="AgencyBookingEngineExportName"/>, which tells the same.
    /// </summary>
    public static string? SourceForExport(string? source) =>
        string.Equals(source, OctorateBookingEngineSource, StringComparison.Ordinal) ? AgencyBookingEngineExportName : source;

    /// <summary>
    /// The values to store for what the checkout sent (E27/F01/US01/T01): the <c>source</c>, the <c>medium</c> and the <c>campaign</c> as lower-case tokens
    /// that fit their columns, the referrer reduced to its host name (<see cref="SignupAttributionRules.SanitizeToken"/>,
    /// <see cref="SignupAttributionRules.ReferrerHostOf"/>). A value that breaks a rule is dropped, never truncated, and never makes the booking fail;
    /// nothing is returned that holds an e-mail address, an address of a page, a phone number (also written with <c>-</c>, <c>_</c> or <c>.</c>), an Italian
    /// fiscal code, a query or a name of a person's machine.
    /// </summary>
    /// <param name="input">What the checkout sent; <c>null</c> when it sent no attribution.</param>
    public static BookingAttributionValues Sanitize(BookingAttributionInput? input)
    {
        if (input is null)
            return BookingAttributionValues.None;

        return new BookingAttributionValues(
            SignupAttributionRules.SanitizeToken(input.Source, MaxSourceLength),
            SignupAttributionRules.SanitizeToken(input.Medium, MaxMediumLength),
            SignupAttributionRules.SanitizeToken(input.Campaign, MaxCampaignLength),
            SignupAttributionRules.ReferrerHostOf(input.Referrer));
    }
}

/// <summary>What the public checkout sends as the attribution of a booking, as it came: nothing in it is trusted before <see cref="BookingAttribution.Sanitize"/>.</summary>
/// <param name="Source">The <c>utm_source</c> of the link.</param>
/// <param name="Medium">The <c>utm_medium</c> of the link.</param>
/// <param name="Campaign">The <c>utm_campaign</c> of the link.</param>
/// <param name="Referrer">The address of the page the guest came from, or its host: only the host name is kept.</param>
public sealed record BookingAttributionInput(string? Source, string? Medium, string? Campaign, string? Referrer);

/// <summary>The attribution of a booking as it is stored: each value <c>null</c> when the link did not carry it or it did not pass the rules.</summary>
/// <param name="Source">Lower-case token, at most <see cref="BookingAttribution.MaxSourceLength"/> characters.</param>
/// <param name="Medium">Lower-case token, at most <see cref="BookingAttribution.MaxMediumLength"/> characters.</param>
/// <param name="Campaign">Lower-case token, at most <see cref="BookingAttribution.MaxCampaignLength"/> characters.</param>
/// <param name="ReferrerHost">Host name in lower case, at most <see cref="BookingAttribution.MaxReferrerHostLength"/> characters.</param>
public sealed record BookingAttributionValues(string? Source, string? Medium, string? Campaign, string? ReferrerHost)
{
    /// <summary>No value at all: the attribution of a booking that came from no link.</summary>
    public static BookingAttributionValues None { get; } = new(null, null, null, null);

    /// <summary>True when there is nothing to store.</summary>
    public bool IsEmpty => Source is null && Medium is null && Campaign is null && ReferrerHost is null;
}
