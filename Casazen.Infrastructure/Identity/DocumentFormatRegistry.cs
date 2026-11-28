using System.Text;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>A format file the registry did not use: its name (never its path or its content) and why, a code of <see cref="DocumentFormatReasonCodes"/>.</summary>
/// <param name="FileName">The name of the file in the folder of the formats.</param>
/// <param name="ReasonCode">Why it was skipped.</param>
public sealed record DocumentFormatRejection(string FileName, string ReasonCode);

/// <summary>
/// The registry of the formats of the identity documents (E70/F01/US02/T02, NF-189, NF-191, decisions D1 and D11): every <c>*.json</c> of
/// <see cref="IdentityVerificationOptions.FormatsDirectory"/> (relative to the application folder, or an absolute path) is a <see cref="DocumentFormat"/>, loaded
/// <b>once</b>, validated, and kept when valid. The Italian electronic identity card (<c>it-cie-3.0</c>) is the first; <b>a new format or a new country is a new
/// file plus a new qualification, never a change of the flow or of this class</b>. No number of a layout is written in C#: the regions and the tolerances come from
/// the files.
/// <para>
/// <b>The assurance matrix.</b> What the verification of a document requires and gives is exactly the configuration of its format: format → required checks
/// (<see cref="DocumentFormat.RequiredChecks"/>: <c>layout</c>, <c>portrait</c>, <c>mrz_check_digits</c>, <c>expiry</c>, <c>side_consistency</c>,
/// <c>field_coherence</c>, <c>booking_consistency</c>) → the level of assurance each kind of channel gives (<see cref="DocumentFormat.Assurance"/>, a level of
/// <see cref="IdentityAssuranceLevels"/>) → the channels admitted (the kinds that are listed). A photograph in a browser gives <c>consistency</c> only: the layout and
/// the check digits are coherent, which a doctored document can also be; <c>cryptographic</c> (the signed data of the chip) is reserved for a future dedicated client
/// and is refused for a browser. Where a segment requires <c>cryptographic</c>, the photo alone leads to review. The human-readable matrix is in the QA matrix of E74.
/// <see cref="AssuranceFor"/> is the question the decision asks: <b>null means the channel is not admitted for the format, and that combination is always reviewed by a
/// person</b>, never approved.
/// </para>
/// <para>
/// <b>Fail closed.</b> Nothing here throws to a caller. A file that is not strict JSON, that is invalid (<see cref="DocumentFormatReasonCodes"/>), that makes its
/// reader or its validator throw (<c>format_json_invalid</c>, whatever it holds: one broken file never hides the others) or whose id another
/// file has too is skipped (every file of a duplicated id, since which one is meant cannot be told) and the others are kept; a missing folder is an empty registry.
/// A document of a format that is not in the registry is <c>format_unsupported</c> and goes to a person. The log has the name of the skipped file and its code, and the
/// ids of the formats loaded: nothing else, and never a value of a document (the registry never sees one: it is asked about the codes of an MRZ, not about its data).
/// </para>
/// </summary>
public sealed class DocumentFormatRegistry
{
    private const int MaxFileBytes = 262_144;

    private const double EdgeTolerance = 1e-9;

    private readonly IOptions<IdentityVerificationOptions> _options;
    private readonly ILogger<DocumentFormatRegistry> _logger;
    private readonly string _baseDirectory;
    private readonly Lazy<Loaded> _loaded;

    public DocumentFormatRegistry(IOptions<IdentityVerificationOptions> options, ILogger<DocumentFormatRegistry> logger)
        : this(options, logger, AppContext.BaseDirectory)
    {
    }

    internal DocumentFormatRegistry(IOptions<IdentityVerificationOptions> options, ILogger<DocumentFormatRegistry> logger, string baseDirectory)
    {
        _options = options;
        _logger = logger;
        _baseDirectory = baseDirectory;
        _loaded = new Lazy<Loaded>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<DocumentFormat> All => _loaded.Value.Formats;

    public IReadOnlyList<DocumentFormatRejection> Rejected => _loaded.Value.Rejected;

    public DocumentFormat? Get(string? id)
    {
        return id is not null && _loaded.Value.ById.TryGetValue(id.Trim(), out var format) ? format : null;
    }

    /// <summary>
    /// The format of a document from the codes of its MRZ, never from its data: the document code (the first two characters of the first line, for example
    /// <c>I&lt;</c> or <c>ID</c>), the issuing state (<c>ITA</c>) and the type of MRZ (<c>TD1</c>, <c>TD2</c> or <c>TD3</c>); the filler <c>&lt;</c>, spaces and the
    /// case do not matter. A format matches when it has this MRZ type and issuing state and a document code prefix the code starts with, and the code does not start
    /// with one of its <see cref="DocumentFormat.ExcludedDocumentCodes"/> (the Italian card claims the codes that start with <c>I</c> and excludes <c>IR</c>, the
    /// residence permit: <c>MrzExtractionService</c> reads it as such); of several formats the one with the
    /// longest prefix wins, and a tie between two formats is no match (which of them is meant cannot be told). <b>Null when nothing matches: the flow answers
    /// <c>format_unsupported</c> and a person decides.</b> A new format file is found here without any change of code.
    /// </summary>
    public DocumentFormat? DetectFromMrz(string? documentCode, string? issuingState, string? mrzType)
    {
        var code = NormalizeMrzField(documentCode);
        var state = NormalizeMrzField(issuingState);
        var type = NormalizeMrzField(mrzType);
        if (code.Length == 0 || state.Length == 0 || type.Length == 0)
            return null;

        DocumentFormat? best = null;
        var bestLength = 0;
        var tie = false;
        foreach (var format in All)
        {
            if (!string.Equals(format.Mrz.Type, type, StringComparison.Ordinal) || !string.Equals(format.Mrz.IssuingState, state, StringComparison.Ordinal))
                continue;

            if (format.ExcludedDocumentCodes is { } excluded && excluded.Any(prefix => code.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            foreach (var prefix in format.DocumentCodes)
            {
                if (!code.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (prefix.Length > bestLength)
                {
                    best = format;
                    bestLength = prefix.Length;
                    tie = false;
                }
                else if (prefix.Length == bestLength && !ReferenceEquals(best, format))
                {
                    tie = true;
                }
            }
        }

        return tie ? null : best;
    }

    /// <summary>
    /// The level of assurance a channel gives for a format (a code of <see cref="IdentityAssuranceLevels"/>: <c>consistency</c> or <c>cryptographic</c>); <paramref name="channel"/>
    /// is a code of <see cref="IdentityChannels"/> (<c>web:ios-safari</c>) or its kind (<c>web</c>). <b>Null when the format is not in the registry or does not admit the
    /// channel: that combination is always reviewed by a person, never approved.</b>
    /// </summary>
    public string? AssuranceFor(string? formatId, string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || Get(formatId) is not { } format)
            return null;

        return format.Assurance.TryGetValue(DocumentFormatChannels.KindOf(channel), out var level) ? level : null;
    }

    /// <summary>
    /// The reason code a format is not valid for, or null when it is. The first thing wrong, in the order of the members of the file; never a value, since a code is
    /// all that is logged.
    /// </summary>
    internal static string? Validate(DocumentFormat format)
    {
        if (!IsFormatId(format.Id))
            return DocumentFormatReasonCodes.FormatIdInvalid;

        if (format.Country.Length != 3 || !format.Country.All(char.IsAsciiLetterUpper))
            return DocumentFormatReasonCodes.FormatCountryInvalid;

        if (string.IsNullOrWhiteSpace(format.Label) || format.Label.Length > 100)
            return DocumentFormatReasonCodes.FormatLabelInvalid;

        if (format.DocumentCodes.Count is 0 or > 8
            || format.DocumentCodes.Any(code => code is null || code.Length is 0 or > 2 || !code.All(char.IsAsciiLetterUpper))
            || format.DocumentCodes.Distinct(StringComparer.Ordinal).Count() != format.DocumentCodes.Count)
        {
            return DocumentFormatReasonCodes.FormatDocumentCodesInvalid;
        }

        if (format.ExcludedDocumentCodes is { } excluded && !AreValidExcludedCodes(excluded, format.DocumentCodes))
            return DocumentFormatReasonCodes.FormatDocumentCodesInvalid;

        var mrz = format.Mrz;
        if (!DocumentFormatMrzTypes.All.Contains(mrz.Type)
            || mrz.IssuingState.Length is 0 or > 3 || !mrz.IssuingState.All(char.IsAsciiLetterUpper)
            || !DocumentFormatSides.All.Contains(mrz.Side))
        {
            return DocumentFormatReasonCodes.FormatMrzInvalid;
        }

        var twoSided = format.Sides.SequenceEqual(DocumentFormatSides.TwoSided, StringComparer.Ordinal);
        var oneSided = format.Sides.SequenceEqual(DocumentFormatSides.OneSided, StringComparer.Ordinal);
        if ((!twoSided && !oneSided) || !format.Sides.Contains(mrz.Side))
            return DocumentFormatReasonCodes.FormatSidesInvalid;

        var layoutFailure = ValidateLayout(format);
        if (layoutFailure is not null)
            return layoutFailure;

        var checks = format.RequiredChecks;
        if (checks.Count == 0 || checks.Any(check => check is null || !DocumentFormatChecks.All.Contains(check)) || checks.Distinct(StringComparer.Ordinal).Count() != checks.Count)
            return DocumentFormatReasonCodes.FormatChecksInvalid;

        foreach (var (kind, level) in format.Assurance)
        {
            if (!DocumentFormatChannels.Kinds.Contains(kind)
                || !IdentityAssuranceLevels.All.Contains(level)
                || (kind == DocumentFormatChannels.Web && level != IdentityAssuranceLevels.Consistency))
            {
                return DocumentFormatReasonCodes.FormatAssuranceInvalid;
            }
        }

        return DocumentFormatStatuses.All.Contains(format.Status) ? null : DocumentFormatReasonCodes.FormatStatusInvalid;
    }

    private static bool AreValidExcludedCodes(IReadOnlyList<string> excluded, IReadOnlyList<string> documentCodes)
    {
        if (excluded.Count > 8 || excluded.Distinct(StringComparer.Ordinal).Count() != excluded.Count)
            return false;

        return excluded.All(code => code is not null
            && code.Length is > 0 and <= 2
            && code.All(char.IsAsciiLetterUpper)
            && !documentCodes.Contains(code, StringComparer.Ordinal)
            && documentCodes.Any(prefix => code.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static string? ValidateLayout(DocumentFormat format)
    {
        var layout = format.Layout;
        if (!(layout.AspectRatio > 0 && layout.AspectRatio <= 10)
            || !(layout.AspectTolerance > 0 && layout.AspectTolerance <= 1)
            || !(layout.MinCardAreaShare > 0 && layout.MinCardAreaShare <= 1)
            || !(layout.MarginMin >= 0 && layout.MarginMin < 0.5))
        {
            return DocumentFormatReasonCodes.FormatLayoutInvalid;
        }

        if (layout.Sides.Count != format.Sides.Count || format.Sides.Any(side => !layout.Sides.ContainsKey(side)))
            return DocumentFormatReasonCodes.FormatLayoutInvalid;

        var portraits = 0;
        var regions = new List<DocumentFormatRegion>();
        foreach (var (side, sideLayout) in layout.Sides)
        {
            if (sideLayout is null)
                return DocumentFormatReasonCodes.FormatLayoutInvalid;

            if ((side == format.Mrz.Side) != (sideLayout.MrzRegion is not null))
                return DocumentFormatReasonCodes.FormatLayoutInvalid;

            if (sideLayout.Portrait is not null)
                portraits++;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in sideLayout.Fields)
            {
                if (field is null || !IsFieldName(field.Name) || !names.Add(field.Name)
                    || field.Labels.Count == 0 || field.Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 80))
                {
                    return DocumentFormatReasonCodes.FormatLayoutInvalid;
                }

                regions.Add(field.Zone);
            }

            if (sideLayout.Portrait is not null)
                regions.Add(sideLayout.Portrait);

            if (sideLayout.MrzRegion is not null)
                regions.Add(sideLayout.MrzRegion);
        }

        if (portraits > 1 || (portraits == 0 && format.RequiredChecks.Contains(DocumentFormatChecks.Portrait)))
            return DocumentFormatReasonCodes.FormatLayoutInvalid;

        return regions.All(IsInsideTheCard) ? null : DocumentFormatReasonCodes.FormatRegionOutOfBounds;
    }

    private static bool IsInsideTheCard(DocumentFormatRegion region)
    {
        return region.X >= 0 && region.Y >= 0 && region.W > 0 && region.H > 0 && region.X + region.W <= 1 + EdgeTolerance && region.Y + region.H <= 1 + EdgeTolerance;
    }

    private static bool IsFormatId(string? value)
    {
        return value is { Length: > 0 and <= 40 }
            && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0]))
            && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-' or '_');
    }

    private static bool IsFieldName(string? value)
    {
        return value is { Length: > 0 and <= 40 } && char.IsAsciiLetterLower(value[0]) && value.All(char.IsAsciiLetterOrDigit);
    }

    private static string NormalizeMrzField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character != '<' && !char.IsWhiteSpace(character))
                builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    private Loaded Load()
    {
        try
        {
            return LoadCore();
        }
        catch (Exception ex)
        {
            _logger.LogError("The identity formats could not be loaded ({ErrorType}); every document goes to a person", ex.GetType().Name);
            return Loaded.Empty;
        }
    }

    private Loaded LoadCore()
    {
        var configured = _options.Value.FormatsDirectory;
        var directory = string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.IsPathRooted(configured.Trim()) ? configured.Trim() : Path.GetFullPath(Path.Combine(_baseDirectory, configured.Trim()));
        if (directory is null || !Directory.Exists(directory))
        {
            _logger.LogWarning("The identity formats folder is missing: reason={Reason}", DocumentFormatReasonCodes.FormatsFolderMissing);
            return Loaded.Empty;
        }

        var rejected = new List<DocumentFormatRejection>();
        var candidates = new List<(string FileName, DocumentFormat Format)>();
        var files = Directory.EnumerateFiles(directory, "*.json")
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .OrderBy(file => file.Name, StringComparer.Ordinal);
        foreach (var (path, name) in files)
        {
            var (format, reason) = ReadGuarded(() => Read(path), _logger, name);
            if (format is null)
                rejected.Add(new DocumentFormatRejection(name, reason!));
            else
                candidates.Add((name, format));
        }

        var duplicated = candidates.GroupBy(candidate => candidate.Format.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate => duplicated.Contains(candidate.Format.Id)))
            rejected.Add(new DocumentFormatRejection(candidate.FileName, DocumentFormatReasonCodes.FormatIdDuplicate));

        var formats = candidates
            .Where(candidate => !duplicated.Contains(candidate.Format.Id))
            .Select(candidate => candidate.Format)
            .OrderBy(format => format.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var rejection in rejected)
            _logger.LogWarning("An identity format file was skipped: file={File} reason={Reason}", rejection.FileName, rejection.ReasonCode);

        _logger.LogInformation("Identity formats loaded: {Count} ({Ids})", formats.Count, string.Join(',', formats.Select(format => format.Id)));
        return new Loaded(formats, rejected, formats.ToDictionary(format => format.Id, StringComparer.Ordinal));
    }

    /// <summary>
    /// The reading of ONE file, run so that whatever that file does to the reader or to the validator never reaches the others: an exception is the code
    /// <c>format_json_invalid</c> of that file (the name of the file and the TYPE of the exception are logged, never its message) and the other formats still load.
    /// </summary>
    internal static (DocumentFormat? Format, string? ReasonCode) ReadGuarded(Func<(DocumentFormat? Format, string? ReasonCode)> read, ILogger logger, string fileName)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogWarning("An identity format file could not be checked: file={File} ({ErrorType})", fileName, ex.GetType().Name);
            return (null, DocumentFormatReasonCodes.FormatJsonInvalid);
        }
    }

    private static (DocumentFormat? Format, string? ReasonCode) Read(string path)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return (null, DocumentFormatReasonCodes.FormatJsonInvalid);

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, DocumentFormatReasonCodes.FormatUnreadable);
        }

        if (!IdentityConfigurationJson.TryCanonicalize(bytes, out var canonical))
            return (null, DocumentFormatReasonCodes.FormatJsonInvalid);

        DocumentFormat? format;
        try
        {
            format = IdentityConfigurationJson.ReadStrict<DocumentFormat>(canonical);
        }
        catch (JsonException)
        {
            return (null, DocumentFormatReasonCodes.FormatJsonInvalid);
        }

        if (format is null)
            return (null, DocumentFormatReasonCodes.FormatJsonInvalid);

        var failure = Validate(format);
        return failure is null ? (format, null) : (null, failure);
    }

    private sealed record Loaded(IReadOnlyList<DocumentFormat> Formats, IReadOnlyList<DocumentFormatRejection> Rejected, IReadOnlyDictionary<string, DocumentFormat> ById)
    {
        public static readonly Loaded Empty = new([], [], new Dictionary<string, DocumentFormat>());
    }
}
