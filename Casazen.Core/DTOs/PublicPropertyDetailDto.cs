namespace Casazen.Core.DTOs;

public class PublicPropertyDetailDto : PublicPropertyDto
{
    public string HouseRules { get; set; } = string.Empty;
    public string CancellationPolicySummary { get; set; } = string.Empty;

    /// <summary>
    /// The cancellation policy in a form a page can draw (DB-03b): the catalog policy with what the host changed of it for a stay under
    /// <c>longStayNights</c> (28) nights, as hours and a percentage. A window the policy does not have is <c>null</c> (<c>fullRefundHours</c>
    /// null = never refunded in full). <c>description</c> is the catalog text, <c>null</c> once the host changed the windows
    /// (<c>hostOverrideApplies</c>). <c>null</c> when the property has no policy. The dates for a stay are in the quote
    /// (<c>cancellation</c>); <see cref="CancellationPolicySummary"/> stays as it was.
    /// </summary>
    public Services.PublicCancellationPolicy? CancellationPolicy { get; set; }

    /// <summary>
    /// Fewest nights a guest can book (DB-03, <c>Property.MinNights</c>), or null for no minimum. The quote and the checkout
    /// refuse a shorter stay with 422 <c>direct_booking_min_nights_not_met</c>.
    /// </summary>
    public int? MinNights { get; set; }

    /// <summary>
    /// Percent added to <see cref="PublicPropertyDto.NightlyRate"/> on the weekend nights (DB-03, <c>Property.WeekendSurchargePercent</c>),
    /// 0 when the host charges none. A weekend night is the night of a Friday or a Saturday, by its Europe/Rome calendar date;
    /// a night costs the nightly rate plus the percentage, rounded to the cent, unless the host priced that date by hand (a
    /// seasonal price the host confirmed costs exactly that price). The quote is the only authority on a total.
    /// </summary>
    public decimal WeekendSurchargePercent { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>A guest can book now: the org of the property can take a payment (same value as <c>PublicOrgDto.AcceptsBookings</c>, DB-03).</summary>
    public bool AcceptsBookings { get; set; }

    /// <summary>How the host is named on the site (same value as <c>PublicOrgDto.HostName</c>, DB-03), or null.</summary>
    public string? HostName { get; set; }

    /// <summary>The phone number the host chose to publish (same value as <c>PublicOrgDto.PublicPhone</c>, DB-03), or null.</summary>
    public string? PublicPhone { get; set; }

    /// <summary>
    /// Absolute URL of the property page on the public domain (<c>App:PublicSiteBaseUrl</c>), without query string, for
    /// the <c>canonical</c> and <c>og:url</c> of the page (BK-15); <c>null</c> only when that is not configured. Set by
    /// the endpoint, never built in the browser.
    /// </summary>
    public string? CanonicalUrl { get; set; }

    /// <summary>
    /// What the host said about the house (DB-11a): specifications, the place, the labels, highlights, safety notes, rules and times, video and
    /// tour. Only what the host filled in: a member the host never said is <c>null</c> (a list: empty) and the page does not draw its section.
    /// Never an address or an exact position.
    /// </summary>
    public PublicPropertyProfileDto Profile { get; set; } = new();

    /// <summary>
    /// The photos of the gallery in display order (the first is the cover), each with its alternative text and size (DB-11a). Lists the same
    /// addresses as <see cref="PublicPropertyDto.PhotoUrls"/>, which stays.
    /// </summary>
    public IReadOnlyList<PublicPropertyPhotoDto> Photos { get; set; } = [];

    /// <summary>
    /// The number and the average mark of the public reviews of the property (DB-12a), set by the endpoint. <b>Absent</b> from the JSON
    /// (not <c>null</c>, not zero) while the property has no review: a page never shows stars or «0 recensioni».
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicRatingSummaryDto? RatingSummary { get; set; }
}

/// <summary>The count and the average (one decimal, 1 to 5) of the public reviews of a property.</summary>
public class PublicRatingSummaryDto
{
    public int Count { get; set; }
    public decimal Average { get; set; }
}
