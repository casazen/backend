namespace Casazen.Web.Configuration;

/// <summary>
/// Configuration for the bookings API (PC-14).
/// </summary>
public class BookingsOptions
{
    public const string SectionName = "Bookings";

    /// <summary>
    /// Default number of bookings per page when the caller omits <c>pageSize</c>. Defaults to 10.
    /// </summary>
    public int DefaultPageSize { get; set; } = 10;
}
