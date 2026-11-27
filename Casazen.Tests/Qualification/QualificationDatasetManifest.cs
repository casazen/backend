using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Tests.Qualification;

// E74/F01/US01/T02 (NF-198, D12): the manifest of a qualification dataset, the file that says WHO was captured, WHEN, WITH WHAT and for WHAT, and where the
// files of every capture are (relative to the folder of the manifest). The toolkit is test-hosted on purpose (no project in the solution): it lives in
// Casazen.Tests/Qualification and reads the manifest with System.Text.Json. Privacy: a manifest holds anonymous ids, codes and RELATIVE paths, never a name,
// a document number, an image or a ground-truth value (those stay in the files it points at, which only the runner reads); nothing here prints or logs
// anything but ids, codes and counts. Codes and constants are never renamed or reused: a new one goes at the end of its list.

public static class QualificationSplits
{
    public const string Development = "development";
    public const string Calibration = "calibration";
    public const string Test = "test";

    public static IReadOnlyList<string> All { get; } = [Development, Calibration, Test];
}

public static class QualificationCaptureKinds
{
    public const string Genuine = "genuine";
    public const string Attack = "attack";

    public static IReadOnlyList<string> All { get; } = [Genuine, Attack];
}

public static class QualificationAttackClasses
{
    public const string PhotoPrint = "photo_print";
    public const string ScreenReplay = "screen_replay";
    public const string VideoReplay = "video_replay";
    public const string Synthetic = "synthetic";
    public const string Mask = "mask";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [PhotoPrint, ScreenReplay, VideoReplay, Synthetic, Mask, Other];
}

/// <summary>
/// The rights the data was collected under (the spec: explicit rights for the purpose, training kept apart from the guest service). Every text is required;
/// <see cref="TrainingUse"/> must be declared AND false (a dataset that may train a model is not a test set for the service). The texts are references, never a
/// name or a signed paper.
/// </summary>
/// <param name="Purpose">What the data may be used for.</param>
/// <param name="ConsentReference">The reference of the consent record kept elsewhere (an id, not the record).</param>
/// <param name="CollectedBy">The team that collected it.</param>
/// <param name="TrainingUse">Whether the data may train a model: false, always.</param>
public sealed record QualificationRights(string? Purpose, string? ConsentReference, string? CollectedBy, bool? TrainingUse);

public sealed record QualificationPerson(string? Id, string? Split);

/// <summary>The document shown in a capture, by relative path. Impostor pairs are derived from <see cref="OwnerPerson"/> against the person of the capture.</summary>
/// <param name="Format">The id of the format in the registry (it-cie-3, ...).</param>
/// <param name="Front">The image of the front, relative to the manifest folder.</param>
/// <param name="Back">The image of the back, when the format has one.</param>
/// <param name="OwnerPerson">The person the document belongs to: it differs from the person of the capture in an impostor pair.</param>
/// <param name="Altered">The document was altered (a critical case if the pipeline approves it).</param>
/// <param name="Foreign">The image is not a document of the format (a foreign image).</param>
/// <param name="GroundTruthFile">The file with the true value of every field: read by the runner ONLY, never printed.</param>
public sealed record QualificationDocument(string? Format, string? Front, string? Back, string? OwnerPerson, bool Altered, bool Foreign, string? GroundTruthFile);

public sealed record QualificationSequence(string? Dir, string? TimestampsFile, IReadOnlyList<string>? Challenge);

public sealed record QualificationCapture(
    string? Id,
    string? Person,
    string? Session,
    string? Device,
    string? Channel,
    string? Kind,
    string? AttackClass,
    QualificationDocument? Document,
    QualificationSequence? Sequence);

/// <summary>
/// The dataset manifest, as the JSON the brief fixes (camel case members): <c>datasetId</c>, <c>version</c>, <c>rights</c>, <c>generalization</c>,
/// <c>persons</c>, <c>captures</c>. Reading is strict: an unknown member is refused (a typo must not become a silent default) and a member that is not there
/// reads as null, so that the validator, not the parser, says what is missing. <see cref="Generalization"/> says that generalization is evaluated: sessions and
/// devices must then be disjoint across the splits too.
/// </summary>
public sealed record QualificationDatasetManifest(
    string? DatasetId,
    string? Version,
    QualificationRights? Rights,
    bool Generalization,
    IReadOnlyList<QualificationPerson>? Persons,
    IReadOnlyList<QualificationCapture>? Captures)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static QualificationDatasetManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return JsonSerializer.Deserialize<QualificationDatasetManifest>(json, Json) ?? throw new QualificationManifestFormatException("$", null);
        }
        catch (JsonException exception)
        {
            throw new QualificationManifestFormatException(exception.Path, exception.LineNumber + 1);
        }
    }

    public static QualificationDatasetManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Parse(File.ReadAllText(path));
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Json);
    }
}

public sealed class QualificationManifestFormatException : Exception
{
    private static readonly IReadOnlyDictionary<string, string> KnownMembers = QualificationJsonPlace.MemberNames(typeof(QualificationDatasetManifest), typeof(QualificationRights), typeof(QualificationPerson), typeof(QualificationCapture), typeof(QualificationDocument), typeof(QualificationSequence));

    public QualificationManifestFormatException(string? jsonPath, long? line)
        : base("The qualification manifest cannot be read (" + QualificationJsonPlace.Describe(QualificationJsonPlace.SafePath(jsonPath, KnownMembers), line) + ").")
    {
        JsonPath = QualificationJsonPlace.SafePath(jsonPath, KnownMembers);
        Line = line;
    }

    public string JsonPath { get; }

    public long? Line { get; }
}
