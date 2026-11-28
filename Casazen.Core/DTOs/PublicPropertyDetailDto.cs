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

    public int? MinNights { get; set; }

    /// <summary>
    /// Percent added to <see cref="PublicPropertyDto.NightlyRate"/> on the weekend nights (DB-03, <c>Property.WeekendSurchargePercent</c>),
    /// 0 when the host charges none. A weekend night is the night of a Friday or a Saturday, by its Europe/Rome calendar date;
    /// a night costs the nightly rate plus the percentage, rounded to the cent, unless the host priced that date by hand (a
    /// seasonal price the host confirmed costs exactly that price). The quote is the only authority on a total.
    /// </summary>
    public decimal WeekendSurchargePercent { get; set; }

    public bool AcceptsBookings { get; set; }

    public string? HostName { get; set; }

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

    public IReadOnlyList<PublicPropertyPhotoDto> Photos { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicRatingSummaryDto? RatingSummary { get; set; }
}

public class PublicRatingSummaryDto
{
    public int Count { get; set; }
    public decimal Average { get; set; }
}
