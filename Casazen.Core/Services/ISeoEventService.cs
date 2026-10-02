namespace Casazen.Core.Services;

/// <summary>An event sent by an SEO page (already checked by the rules of the signup attribution).</summary>
/// <param name="Event">The wire name: <c>cta_click</c> or <c>signup_start</c>.</param>
/// <param name="Comune">Slug or ISTAT code of the comune of the page.</param>
public sealed record SeoEventInput(
    string Event,
    string Comune,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? ReferrerHost);

/// <summary>A row of the admin report: what one comune did in the window.</summary>
public sealed record SeoTopComune(string ComuneCode, string ComuneName, int CtaClicks, int SignupStarts, int Signups);

/// <summary>The admin report: comuni by CTA clicks, the window and how long the events are kept.</summary>
public sealed record SeoTopComuniResult(
    int Days,
    DateTime From,
    DateTime To,
    int RetentionDays,
    IReadOnlyList<SeoTopComune> Items);

/// <summary>Events of the SEO funnel (SE-04, #300 AC3 and AC9): recording, the admin report and the retention.</summary>
public interface ISeoEventService
{
    /// <summary>Code of the 422 for an event name that is not <c>cta_click</c> or <c>signup_start</c>.</summary>
    public const string UnknownEventCode = "seo_event_unknown";

    /// <summary>Code of the 422 for a well-formed comune that CasaZen does not know (nothing is stored).</summary>
    public const string UnknownComuneCode = "seo_event_unknown_comune";

    /// <summary>
    /// Records one event with no personal data: the event, the comune and the marketing values of the visit.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// The event name is unknown (<see cref="UnknownEventCode"/>) or the comune is (<see cref="UnknownComuneCode"/>).
    /// </exception>
    Task RecordAsync(SeoEventInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Comuni by CTA clicks in the last <paramref name="days"/> days (at most the retention period), best first, at most
    /// <paramref name="limit"/>, each with the started signups and the host signups attributed to it (SE-03).
    /// </summary>
    Task<SeoTopComuniResult> GetTopComuniAsync(int days, int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes the events older than the retention period (<c>Seo:Events:RetentionDays</c>); returns how many.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
