namespace Casazen.Infrastructure.Http;

/// <summary>
/// Limits of <see cref="SafeExternalHttpClient"/>, the client for URLs chosen by users (iCal feeds), bound from
/// the <c>SafeExternalHttp</c> configuration section (Railway: <c>SafeExternalHttp__MaxResponseBytes</c>, ...).
/// Every value has a safe default: the section is optional. See <c>docs/runbooks/external-fetch.md</c>.
/// </summary>
public sealed class SafeExternalHttpOptions
{
    public const string SectionName = "SafeExternalHttp";

    public int[]? AllowedPorts { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public long MaxResponseBytes { get; set; } = 5L * 1024 * 1024;

    public int MaxRedirects { get; set; } = 3;

    internal static readonly int[] DefaultAllowedPorts = [443];

    internal IReadOnlyCollection<int> EffectiveAllowedPorts =>
        AllowedPorts is { Length: > 0 } ports ? ports : DefaultAllowedPorts;

    internal TimeSpan EffectiveTimeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 120));

    internal long EffectiveMaxResponseBytes => MaxResponseBytes > 0 ? MaxResponseBytes : 5L * 1024 * 1024;

    internal int EffectiveMaxRedirects => Math.Clamp(MaxRedirects, 0, 10);
}
