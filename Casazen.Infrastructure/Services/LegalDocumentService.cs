using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Versions, dates and texts of the legal documents (PL-14, LEGAL-TEXTS). The texts are never written by code: an HTML
/// fragment per version and language under <c>Legal:ContentPath</c> (<c>{kind}/{version}.{lang}.html</c>, shipped with
/// the API), with placeholders for the controller's data and the real plans (<see cref="LegalDocumentTemplate"/>,
/// <see cref="LegalVariables"/>). The drafts were written by an AI agent for the product owner (D14, updated 2026-10-01)
/// and need a lawyer's review before production use.
/// <para>
/// <b>Fail-closed.</b> A text is served only when the file of the <i>configured</i> version exists and every value it
/// needs is configured; otherwise the clients show "in preparation" (never a placeholder), the health check
/// <c>legal</c> is degraded and the startup log says what to set (D9). The consent always refers to the configured
/// version: publishing a new text means changing the version (re-acceptance by the hosts, PL-02), a value of the
/// controller's data does not. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </para>
/// </summary>
public partial class LegalDocumentService(IConfiguration configuration, ILogger<LegalDocumentService> logger)
    : ILegalDocumentService
{
    public const string DefaultContentPath = "LegalDocuments";
    public const string DefaultLanguage = "it";
    private static readonly string[] SupportedLanguages = [DefaultLanguage, "en"];

    // Texts and values change only with a deploy: one read and rendering per file and process.
    private readonly ConcurrentDictionary<string, TextEntry> _entries = new(StringComparer.Ordinal);

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
        if (!IsSafeVersion(version))
        {
            logger.LogWarning("Legal document {Kind}: version {Version} is not a valid file name, no text served", kind, version);
            return null;
        }

        var requested = NormalizeLanguage(language);
        string[] candidates = requested == DefaultLanguage ? [DefaultLanguage] : [requested, DefaultLanguage];
        foreach (var candidate in candidates)
        {
            var text = GetEntry(kind, version, candidate).Text;
            if (text is not null)
                return text;
        }

        return null;
    }

    public LegalDocumentPublication GetPublication(LegalDocumentKind kind)
    {
        var version = GetVersion(kind.ToString());
        var hasExternalCopy = ReadHttpsUrl($"Legal:Documents:{kind}:DocumentUrl") is not null;
        if (!IsSafeVersion(version))
        {
            return new LegalDocumentPublication(
                kind, version, HasText: false, hasExternalCopy, TextFileFound: false, [],
                ["the version is not a valid file name (letters, digits, '.', '_', '+', '-')"]);
        }

        var italian = GetEntry(kind, version, DefaultLanguage);
        var missing = new SortedSet<string>(italian.Missing, StringComparer.Ordinal);
        var problems = new List<string>(italian.Problems);

        // A translation is optional, but one that cannot be published is a problem to report: the English reader would
        // silently get the Italian text.
        foreach (var language in SupportedLanguages.Where(l => l != DefaultLanguage))
        {
            var translation = GetEntry(kind, version, language);
            if (!translation.FileFound || translation.Text is not null)
                continue;

            missing.UnionWith(translation.Missing);
            problems.AddRange(translation.Problems.Select(p => $"{language}: {p}"));
        }

        return new LegalDocumentPublication(
            kind, version, HasText: italian.Text is not null, hasExternalCopy, italian.FileFound, [.. missing], problems);
    }

    private TextEntry GetEntry(LegalDocumentKind kind, string version, string language)
    {
        var path = Path.Combine(ContentRoot(), kind.ToString().ToLowerInvariant(), $"{version}.{language}.html");
        return _entries.GetOrAdd(path, p => ReadText(kind, p, language));
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

    private TextEntry ReadText(LegalDocumentKind kind, string path, string language)
    {
        try
        {
            if (!File.Exists(path))
                return TextEntry.NotFound;

            var rendered = LegalDocumentTemplate.Render(File.ReadAllText(path), LegalVariables.Create(configuration, language));
            if (!rendered.IsComplete)
            {
                // Fail-closed: no text is better than a text with a placeholder (names only, never values).
                logger.LogWarning(
                    "Legal document {Kind} ({Language}) is not published: missing or invalid {Missing}; problems: {Problems}. " +
                    "The public page stays 'in preparation' (docs/runbooks/legal-documents.md)",
                    kind, language, string.Join(", ", rendered.MissingConfiguration), string.Join("; ", rendered.Problems));
                return new TextEntry(FileFound: true, Text: null, rendered.MissingConfiguration, rendered.Problems);
            }

            // Same allowlist as the public editorial pages: the frontend sanitizes again before rendering.
            var html = SeoHtmlSanitizer.Sanitize(rendered.Html);
            return html.Length == 0
                ? new TextEntry(FileFound: true, Text: null, [], ["the text is empty"])
                : new TextEntry(FileFound: true, new LegalDocumentText(language, html), [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Legal document text {Path} cannot be read", path);
            return new TextEntry(FileFound: true, Text: null, [], ["the text file cannot be read"]);
        }
    }

    private static bool IsSafeVersion(string version) =>
        SafeVersionRegex().IsMatch(version) && !version.Contains("..", StringComparison.Ordinal);

    private sealed record TextEntry(
        bool FileFound,
        LegalDocumentText? Text,
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Problems)
    {
        public static TextEntry NotFound { get; } = new(false, null, [], []);
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
