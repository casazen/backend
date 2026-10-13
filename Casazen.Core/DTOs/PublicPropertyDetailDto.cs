namespace Casazen.Core.DTOs;

public class PublicPropertyDetailDto : PublicPropertyDto
{
    public string HouseRules { get; set; } = string.Empty;
    public string CancellationPolicySummary { get; set; } = string.Empty;

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
}
