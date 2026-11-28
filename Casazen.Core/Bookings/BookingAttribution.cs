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
    public const int MaxSourceLength = 40;

    public const int MaxMediumLength = 40;

    public const int MaxCampaignLength = 80;

    public const int MaxReferrerHostLength = 255;

    public const string AgencySiteSource = "agency-site";

    public const string OctorateBookingEngineSource = "octorate-booking-engine";

    public static IReadOnlyList<string> AgencySources { get; } = [AgencySiteSource, OctorateBookingEngineSource];

    public static bool IsAgencySource(string? source) =>
        source is not null && AgencySources.Contains(source, StringComparer.Ordinal);

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

public sealed record BookingAttributionInput(string? Source, string? Medium, string? Campaign, string? Referrer);

public sealed record BookingAttributionValues(string? Source, string? Medium, string? Campaign, string? ReferrerHost)
{
    public static BookingAttributionValues None { get; } = new(null, null, null, null);

    public bool IsEmpty => Source is null && Medium is null && Campaign is null && ReferrerHost is null;
}
