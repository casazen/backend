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

public sealed record SeoTopComune(string ComuneCode, string ComuneName, int CtaClicks, int SignupStarts, int Signups);

public sealed record SeoTopComuniResult(
    int Days,
    DateTime From,
    DateTime To,
    int RetentionDays,
    IReadOnlyList<SeoTopComune> Items);

public interface ISeoEventService
{
    public const string UnknownEventCode = "seo_event_unknown";

    public const string UnknownComuneCode = "seo_event_unknown_comune";

    Task RecordAsync(SeoEventInput input, CancellationToken cancellationToken = default);

    Task<SeoTopComuniResult> GetTopComuniAsync(int days, int limit, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
