using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Versions, dates and texts of the legal documents (PL-14). The texts are provided by the product owner (D14) and
/// never written by code: an HTML fragment per version and language under <c>Legal:ContentPath</c>
/// (<c>{kind}/{version}.{lang}.html</c>, shipped with the API). While a text is missing the clients show "in
/// preparation" and the consent still refers to the configured version. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public partial class LegalDocumentService(IConfiguration configuration, ILogger<LegalDocumentService> logger)
    : ILegalDocumentService
{
    public const string DefaultContentPath = "LegalDocuments";
    public const string DefaultLanguage = "it";
    private static readonly string[] SupportedLanguages = [DefaultLanguage, "en"];

    // Texts change only with a deploy: one read per file and process.
    private readonly ConcurrentDictionary<string, LegalDocumentText?> _texts = new(StringComparer.Ordinal);

    private string GetVersion(string key) =>
        configuration[$"Legal:Documents:{key}:Version"] ?? "1.0";

    public LegalDocumentMeta GetTos() => Get(LegalDocumentKind.Tos);

    public LegalDocumentMeta GetPrivacy() => Get(LegalDocumentKind.Privacy);

    public LegalDocumentMeta GetDpa() => Get(LegalDocumentKind.Dpa);

    public LegalDocumentMeta Get(LegalDocumentKind kind)
    {
        var key = kind.ToString();
        var (title, summary) = kind switch
        {
            LegalDocumentKind.Tos => ("Termini di Servizio", "Condizioni generali di utilizzo della piattaforma CasaZen."),
            LegalDocumentKind.Privacy => ("Informativa Privacy", "Informativa sul trattamento dei dati personali ai sensi del GDPR."),
            LegalDocumentKind.Dpa => ("Data Processing Agreement", "Accordo sul trattamento dei dati (Art. 28 GDPR)."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        return new LegalDocumentMeta(
            Version: GetVersion(key),
            EffectiveAt: ReadDate($"Legal:Documents:{key}:EffectiveAt"),
            Title: title,
            Summary: summary,
            DocumentUrl: ReadHttpsUrl($"Legal:Documents:{key}:DocumentUrl"));
    }

    public LegalDocumentText? GetText(LegalDocumentKind kind, string? language)
    {
        var version = GetVersion(kind.ToString());
        if (!SafeVersionRegex().IsMatch(version) || version.Contains("..", StringComparison.Ordinal))
        {
            logger.LogWarning("Legal document {Kind}: version {Version} is not a valid file name, no text served", kind, version);
            return null;
        }

        var requested = NormalizeLanguage(language);
        string[] candidates = requested == DefaultLanguage ? [DefaultLanguage] : [requested, DefaultLanguage];
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(ContentRoot(), kind.ToString().ToLowerInvariant(), $"{version}.{candidate}.html");
            var text = _texts.GetOrAdd(path, p => ReadText(p, candidate));
            if (text is not null)
                return text;
        }

        return null;
    }

    /// <summary>
    /// The subprocessors the running configuration uses (<see cref="LegalSubprocessorCatalog"/>). When an external AI
    /// provider is active the version gets <c>+ai-{provider}</c>, so the list acknowledged during the onboarding changes
    /// with it (A8-15).
    /// </summary>
    public SubprocessorsDocument GetSubprocessors()
    {
        var version = GetVersion("Subprocessors");
        var items = LegalSubprocessorCatalog.Build(configuration);

        var ai = items.FirstOrDefault(item => item.Key == LegalSubprocessorCatalog.Ai);
        if (ai is not null)
            version = $"{version}+ai-{ai.Name.ToLowerInvariant().Replace(' ', '-')}";

        return new SubprocessorsDocument(version, ReadDate("Legal:Documents:Subprocessors:EffectiveAt"), items);
    }

    private string ContentRoot()
    {
        var configured = configuration["Legal:ContentPath"];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultContentPath : configured.Trim();
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    private LegalDocumentText? ReadText(string path, string language)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // Same allowlist as the public editorial pages: the frontend sanitizes again before rendering.
            var html = SeoHtmlSanitizer.Sanitize(File.ReadAllText(path));
            return html.Length == 0 ? null : new LegalDocumentText(language, html);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Legal document text {Path} cannot be read", path);
            return null;
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        var value = string.IsNullOrWhiteSpace(language)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language.Trim();
        var twoLetters = value.Length >= 2 ? value[..2].ToLowerInvariant() : value.ToLowerInvariant();
        return SupportedLanguages.Contains(twoLetters) ? twoLetters : DefaultLanguage;
    }

    private DateTime? ReadDate(string key)
    {
        var value = configuration[key];
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
    }

    private string? ReadHttpsUrl(string key) =>
        Uri.TryCreate(configuration[key]?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]*$")]
    private static partial Regex SafeVersionRegex();
}
