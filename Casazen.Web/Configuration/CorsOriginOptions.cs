using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Browser origins allowed to call the API, from the <c>Cors</c> section (FD-17, A3-29 / A9-28). No origin is written in
/// code (decision D3). Runbook: <c>docs/runbooks/cors-security-headers.md</c>.
/// </summary>
public sealed class CorsOriginOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// <c>Cors:AllowedOrigins</c>: exact origins (<c>https://host[:port]</c>), comma separated or as an array
    /// (<c>Cors__AllowedOrigins__0</c>).
    /// </summary>
    public List<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// <c>Cors:VercelPreviewPattern</c>: regular expression for the Vercel preview deployments of our own project. It
    /// must match the <b>whole</b> label before <c>.vercel.app</c> (it is anchored and case-insensitive), and only https
    /// origins on the default port are considered. Empty: no preview deployment is allowed.
    /// </summary>
    public string? VercelPreviewPattern { get; set; }

    /// <summary>Reads the section: the origin list accepts both an array and a comma-separated string.</summary>
    public static void Configure(CorsOriginOptions options, IConfiguration section)
    {
        var origins = section.GetSection(nameof(AllowedOrigins));
        var values = string.IsNullOrWhiteSpace(origins.Value)
            ? origins.GetChildren().Select(child => child.Value)
            : [origins.Value];

        options.AllowedOrigins.Clear();
        options.AllowedOrigins.AddRange(values.SelectMany(value => (value ?? string.Empty).Split(
            [',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        options.VercelPreviewPattern = section[nameof(VercelPreviewPattern)];
    }

    /// <summary>
    /// SE-02 (A8-02): the public web app (<c>App:PublicSiteBaseUrl</c>, alias <c>Seo:PublicBaseUrl</c>) is the site
    /// that calls the API, so its origin is always allowed: CORS follows the configured public domain without repeating
    /// it in <c>Cors:AllowedOrigins</c>. A missing or invalid value adds nothing (the email/public site validation
    /// reports it).
    /// </summary>
    public static void AddPublicSiteOrigin(CorsOriginOptions options, IConfiguration configuration)
    {
        var publicSiteBaseUrl = PublicSiteOptions.ResolveBaseUrl(configuration);
        if (!RequiredConfiguration.IsMissing(publicSiteBaseUrl)
            && PublicSiteOptions.TryGetOrigin(publicSiteBaseUrl, out var origin)
            && !options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            options.AllowedOrigins.Add(origin);
        }
    }
}

/// <summary>
/// The configured origins, parsed once. <see cref="IsOriginAllowed"/> is the single decision point for the CORS policy
/// (<see cref="Infrastructure.CasazenCorsPolicyProvider"/>).
/// </summary>
public sealed class CorsOriginAllowList
{
    private const string VercelSuffix = ".vercel.app";
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    private readonly HashSet<string> _origins;
    private readonly Regex? _vercelPreviewLabel;

    public CorsOriginAllowList(IOptions<CorsOriginOptions> options, IHostEnvironment environment)
        : this(options.Value, environment.IsDevelopment())
    {
    }

    /// <param name="options">The <c>Cors</c> section.</param>
    /// <param name="allowLoopback">
    /// Development only: any <c>http://localhost:port</c> (or loopback IP) origin, for the Vite dev server.
    /// </param>
    public CorsOriginAllowList(CorsOriginOptions options, bool allowLoopback)
    {
        var (origins, pattern, errors) = Parse(options);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        _origins = origins;
        _vercelPreviewLabel = pattern;
        AllowsLoopback = allowLoopback;
    }

    /// <summary>The configured origins, normalized (<c>scheme://host[:port]</c>, lower case).</summary>
    public IReadOnlyCollection<string> Origins => _origins;

    public bool AllowsVercelPreviews => _vercelPreviewLabel is not null;

    public bool AllowsLoopback { get; }

    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (_origins.Contains(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !IsBareOrigin(uri))
            return false;

        if (AllowsLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            return true;

        return IsOwnVercelPreview(uri);
    }

    /// <summary>Configuration errors (malformed origin, invalid pattern); empty when the section is valid.</summary>
    public static IReadOnlyList<string> GetErrors(CorsOriginOptions options) => Parse(options).Errors;

    private bool IsOwnVercelPreview(Uri uri)
    {
        if (_vercelPreviewLabel is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
            return false;

        var host = uri.IdnHost;
        if (!host.EndsWith(VercelSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Only the single label right before .vercel.app: "evil.casazen-app-x.vercel.app" is never a preview.
        var label = host[..^VercelSuffix.Length];
        if (label.Length == 0 || label.Contains('.'))
            return false;

        try
        {
            return _vercelPreviewLabel.IsMatch(label);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsBareOrigin(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static (HashSet<string> Origins, Regex? Pattern, List<string> Errors) Parse(CorsOriginOptions options)
    {
        var errors = new List<string>();
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in options.AllowedOrigins)
        {
            // Placeholders of the example files (https://your-frontend.vercel.app) are not origins.
            if (RequiredConfiguration.IsMissing(value))
                continue;

            if (value.Contains('*', StringComparison.Ordinal)
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || !IsBareOrigin(uri))
            {
                errors.Add(
                    $"Cors__AllowedOrigins contains '{value}', which is not an origin: use scheme://host[:port] " +
                    "(https, no path, no wildcard; preview deployments go in Cors__VercelPreviewPattern).");
                continue;
            }

            origins.Add($"{uri.Scheme}://{uri.Authority}".ToLowerInvariant());
        }

        Regex? pattern = null;
        if (!RequiredConfiguration.IsMissing(options.VercelPreviewPattern))
        {
            try
            {
                pattern = new Regex(
                    $"^(?:{options.VercelPreviewPattern!.Trim()})$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    PatternTimeout);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Cors__VercelPreviewPattern is not a valid regular expression: {ex.Message}");
            }
        }

        return (origins, pattern, errors);
    }
}

/// <summary>
/// Startup validation of the <c>Cors</c> section: a malformed value always stops the startup (a typo would otherwise
/// reject the web app silently); outside Development and Testing at least one origin is required.
/// </summary>
public sealed class CorsOriginOptionsValidator(IHostEnvironment environment) : IValidateOptions<CorsOriginOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOriginOptions options)
    {
        var errors = CorsOriginAllowList.GetErrors(options).ToList();

        if (RequiredConfiguration.IsEnforced(environment)
            && options.AllowedOrigins.All(RequiredConfiguration.IsMissing))
        {
            errors.Add(
                "Cors__AllowedOrigins is missing: set the web app origins of this environment " +
                "(e.g. https://<project>.vercel.app) or App__PublicSiteBaseUrl, otherwise every browser call is " +
                "rejected by CORS.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
