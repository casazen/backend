namespace Casazen.Core.Services;

/// <summary>
/// The calendars of the host's properties in one read (SR-10b, <c>GET /api/properties/calendar-overview</c>): for each active
/// short-rent property of the caller's scope, its iCal import feeds (the state the dashboard shows, never the import URL) and the
/// token of its export link, the link the host pastes on the portals.
/// </summary>
/// <param name="Properties">By name. A property with no feed is in it too, with no feeds.</param>
public sealed record HostCalendarOverview(IReadOnlyList<HostCalendarOverviewProperty> Properties);

/// <summary>One property of <see cref="HostCalendarOverview"/>.</summary>
/// <param name="ExportToken">
/// Token of the export link of the property; null while nobody asked for the link (it is created on first use and this read
/// creates nothing). The link gives read access to the dates taken: a secret in practice, so it is shown to who may read the
/// property and never written to a log.
/// </param>
/// <param name="Feeds">The feeds of the property, those with an error first (the order of <see cref="HostDashboardIcalFeed"/> lists).</param>
public sealed record HostCalendarOverviewProperty(
    Guid PropertyId,
    string PropertyName,
    Guid? ExportToken,
    IReadOnlyList<HostDashboardIcalFeed> Feeds);

/// <summary>
/// What the calendars page may say about the sync of the feeds, declared once where the job is registered so that the page never
/// promises a frequency of its own: the recurring job <c>property-ical-sync</c> tries every feed with a URL every
/// <see cref="SyncEveryMinutes"/> minutes, and a feed whose last try (a success or a failure, <c>LastImportAt</c>) is older than
/// <see cref="StaleAfterMinutes"/> has been left out of eight rounds in a row: the page marks it "to check".
/// </summary>
public static class PropertyICalSchedule
{
    /// <summary>Minutes between two rounds of the recurring job <c>property-ical-sync</c>.</summary>
    public const int SyncEveryMinutes = 15;

    /// <summary>The cron expression of <c>property-ical-sync</c> (UTC), made from <see cref="SyncEveryMinutes"/>.</summary>
    public static readonly string SyncCron = $"*/{SyncEveryMinutes} * * * *";

    /// <summary>A feed not tried for this long is marked "to check" (eight rounds of the job).</summary>
    public const int StaleAfterMinutes = 8 * SyncEveryMinutes;
}
