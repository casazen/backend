using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICheckInLegalTexts" />
/// <remarks>
/// <para><b>Files.</b> <c>{Legal:ContentPath}/checkin/manifest.json</c> (default <c>LegalDocuments</c> next to the API, the folder of the other legal documents) and the Markdown file of each
/// language it names. The manifest lists the versions of each kind in time order: the <b>last</b> one that can be served is the active one. Production serves the last <b>approved</b> one;
/// every other environment (<c>Staging</c>, <c>Development</c>, <c>Testing</c>) the last one, draft or approved, flagged with <c>isDraft</c>. The texts change only with a deploy: they are read
/// once per process.</para>
/// <para><b>Integrity.</b> A language is served only when the SHA-256 of its file (line endings as <c>\n</c>, so a checkout on Windows and one on Linux agree) is the one of the manifest;
/// an approved entry must say who approved it and when; a version needs its Italian file, the reference. What does not hold is logged (names and versions only, never a text) and
/// not served: a text that was changed after it was recorded is never shown to a guest.</para>
/// <para><b>Fail-closed.</b> A missing or unreadable manifest serves nothing; the callers then keep the identity step closed and hand the guest to an operator
/// (<c>legal_text_not_approved</c>).</para>
/// </remarks>
public sealed partial class CheckInLegalTexts(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<CheckInLegalTexts> logger) : ICheckInLegalTexts
{
    /// <summary>The folder of the registry under the folder of the legal documents.</summary>
    public const string FolderName = "checkin";

    /// <summary>The manifest of the registry in <see cref="FolderName"/>.</summary>
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The language the guest asked for, then English, then Italian (the reference of the legal texts).
    private static readonly string[] FallbackLanguages = ["en", GuestLanguages.Default];

    private readonly ConcurrentDictionary<(string Kind, string Version, string Language, bool NotArchived), string> _rendered = new();
    private readonly object _loadLock = new();
    private volatile Registry? _loaded;

    public IReadOnlyList<CheckInLegalText> GetForStay(bool documentNotArchived, string? language)
    {
        var requested = GuestLanguages.Normalize(language) ?? GuestLanguages.Default;
        var texts = new List<CheckInLegalText>();
        foreach (var kind in CheckInLegalTextKinds.All)
        {
            // The Ticino notice is for a property that keeps no document; nobody else is told about Switzerland.
            if (kind == CheckInLegalTextKinds.TicinoNotice && !documentNotArchived)
                continue;

            if (Active(kind) is { } version && Serve(version, requested, documentNotArchived) is { } text)
                texts.Add(text);
        }

        return texts;
    }

    public CheckInLegalVersion? GetActiveVersion(string kind) =>
        Active(kind) is { } version ? new CheckInLegalVersion(version.Kind, version.Version, version.IsDraft, version.ReferenceHash) : null;

    public CheckInLegalVersion? FindServedVersion(string kind, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || Active(kind) is not { } active)
            return null;

        return string.Equals(active.Version, version.Trim(), StringComparison.Ordinal)
            ? new CheckInLegalVersion(active.Kind, active.Version, active.IsDraft, active.ReferenceHash)
            : null;
    }

    /// <summary>
    /// The version served now for a kind: the last approved one in production, the last one elsewhere. A version without a text that can be served
    /// (the Italian file is the reference) was left out when the registry was read.
    /// </summary>
    private TextVersion? Active(string kind)
    {
        if (!CheckInLegalTextKinds.IsKnown(kind))
            return null;

        var production = environment.IsProduction();
        return Current().Versions.LastOrDefault(v => v.Kind == kind && (!production || !v.IsDraft));
    }

    private CheckInLegalText? Serve(TextVersion version, string requested, bool documentNotArchived)
    {
        foreach (var language in new[] { requested }.Concat(FallbackLanguages).Distinct(StringComparer.Ordinal))
        {
            if (!version.Markdown.TryGetValue(language, out var markdown))
                continue;

            var html = _rendered.GetOrAdd(
                (version.Kind, version.Version, language, documentNotArchived),
                _ => SeoHtmlSanitizer.Sanitize(CheckInLegalMarkdown.ToHtml(markdown, documentNotArchived)));
            return html.Length == 0 ? null : new CheckInLegalText(version.Kind, version.Version, language, version.IsDraft, html);
        }

        return null;
    }

    private Registry Current()
    {
        if (_loaded is { } loaded)
            return loaded;

        lock (_loadLock)
            return _loaded ??= Load();
    }

    private Registry Load()
    {
        var folder = Path.Combine(ContentRoot(), FolderName);
        var manifestPath = Path.Combine(folder, ManifestFileName);
        Manifest? manifest;
        try
        {
            if (!File.Exists(manifestPath))
            {
                logger.LogWarning("Check-in legal texts: no manifest in {Folder}, no text is served", folder);
                return Registry.Empty;
            }

            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath, Encoding.UTF8), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(ex, "Check-in legal texts: the manifest in {Folder} cannot be read, no text is served", folder);
            return Registry.Empty;
        }

        var versions = new List<TextVersion>();
        foreach (var entry in manifest?.Texts ?? [])
        {
            if (TryBuild(entry, folder, out var version, out var problem) && version is not null)
            {
                if (versions.Any(v => v.Kind == version.Kind && v.Version == version.Version))
                    logger.LogError("Check-in legal text {Kind} {Version} is listed twice in the manifest: the second one is not served", version.Kind, version.Version);
                else
                    versions.Add(version);

                continue;
            }

            logger.LogError(
                "Check-in legal text {Kind} {Version} is not served: {Problem} (docs/runbooks/legal-documents.md)",
                entry.Kind, entry.Version, problem);
        }

        if (environment.IsProduction())
        {
            foreach (var kind in CheckInLegalTextKinds.All.Where(k => !versions.Any(v => v.Kind == k && !v.IsDraft)))
            {
                logger.LogWarning(
                    "Check-in legal text {Kind} has no approved version: production serves nothing for it" +
                    " (the identity verification is not offered while the consent is missing)",
                    kind);
            }
        }

        return new Registry(versions);
    }

    private static bool TryBuild(ManifestText entry, string folder, out TextVersion? version, out string problem)
    {
        version = null;
        if (!CheckInLegalTextKinds.IsKnown(entry.Kind))
        {
            problem = "unknown kind";
            return false;
        }

        var id = entry.Version?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > BiometricConsentRules.VersionMaxLength || !SafeVersionRegex().IsMatch(id))
        {
            problem = $"the version is not an id of at most {BiometricConsentRules.VersionMaxLength} characters (letters, digits, '.', '_', '-')";
            return false;
        }

        bool draft;
        if (string.Equals(entry.Status, CheckInLegalTextStatuses.Draft, StringComparison.Ordinal))
        {
            draft = true;
        }
        else if (string.Equals(entry.Status, CheckInLegalTextStatuses.Approved, StringComparison.Ordinal))
        {
            // The approval is part of the text: who approved it and when, written by the pull request that approves it.
            if (string.IsNullOrWhiteSpace(entry.ApprovedBy)
                || !DateTimeOffset.TryParse(entry.ApprovedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                problem = "approved without approvedBy and approvedAt";
                return false;
            }

            draft = false;
        }
        else
        {
            problem = "the status is neither draft nor approved";
            return false;
        }

        var markdown = new Dictionary<string, string>(StringComparer.Ordinal);
        string? referenceHash = null;
        foreach (var (language, file) in entry.Languages ?? new Dictionary<string, ManifestFile>())
        {
            if (GuestLanguages.Normalize(language) != language)
            {
                problem = $"language '{language}' is not one of the languages of the check-in page";
                return false;
            }

            if (!TryRead(folder, file, out var text, out var hash, out var readProblem))
            {
                problem = $"{language}: {readProblem}";
                return false;
            }

            markdown[language] = text;
            if (language == GuestLanguages.Default)
                referenceHash = hash;
        }

        if (referenceHash is null)
        {
            problem = "the Italian text, the reference, is missing";
            return false;
        }

        version = new TextVersion(entry.Kind!, id, draft, referenceHash, markdown);
        problem = string.Empty;
        return true;
    }

    /// <summary>The text of a file of the registry and its SHA-256, only when the file is where the manifest says and its hash is the recorded one.</summary>
    private static bool TryRead(string folder, ManifestFile? file, out string text, out string hash, out string problem)
    {
        text = string.Empty;
        hash = string.Empty;
        var name = file?.File?.Trim();
        if (string.IsNullOrEmpty(name)
            || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            problem = "the file is not a name of a .md file of the folder";
            return false;
        }

        try
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
                problem = "the file does not exist";
                return false;
            }

            hash = Sha256OfText(File.ReadAllBytes(path), out text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "the file cannot be read";
            return false;
        }

        if (!string.Equals(hash, file!.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            problem = "the SHA-256 of the file is not the one of the manifest";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// The SHA-256 (hex, lower case) of a text file as its text: UTF-8 without the byte order mark and with <c>\n</c> line endings, so that the hash does not depend on the checkout.
    /// <paramref name="text"/> is that text.
    /// </summary>
    public static string Sha256OfText(byte[] bytes, out string text)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var normalized = new List<byte>(bytes.Length);
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\r')
                normalized.Add(bytes[i]);
        }

        var array = normalized.ToArray();
        text = Encoding.UTF8.GetString(array);
        return Convert.ToHexString(SHA256.HashData(array)).ToLowerInvariant();
    }

    private string ContentRoot()
    {
        var configured = configuration["Legal:ContentPath"];
        var path = string.IsNullOrWhiteSpace(configured) ? LegalDocumentService.DefaultContentPath : configured.Trim();
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeVersionRegex();

    private sealed record TextVersion(string Kind, string Version, bool IsDraft, string ReferenceHash, IReadOnlyDictionary<string, string> Markdown);

    private sealed record Registry(IReadOnlyList<TextVersion> Versions)
    {
        public static Registry Empty { get; } = new([]);
    }

    private sealed class Manifest
    {
        public List<ManifestText>? Texts { get; set; }
    }

    private sealed class ManifestText
    {
        public string? Kind { get; set; }

        public string? Version { get; set; }

        public string? Status { get; set; }

        public string? ApprovedBy { get; set; }

        public string? ApprovedAt { get; set; }

        public Dictionary<string, ManifestFile>? Languages { get; set; }
    }

    private sealed class ManifestFile
    {
        public string? File { get; set; }

        public string? Sha256 { get; set; }
    }
}
