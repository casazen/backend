using System.Globalization;
using System.Text.Json;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T02 (NF-198, D12): «Soglia e regola di aggregazione congelate prima del test finale … La versione candidata comprende pesi, soglie e report. Ogni nuova versione dei
// pesi o delle soglie ripete la qualificazione.» The lock is that freeze, written down: the policy (its version and canonical hash), the weights of the three models (and of the
// candidates that are loaded), the aggregation rule, the thresholds, the dataset (the hash of its manifest and how many persons the test split has) and the day it was frozen. The
// final test on the test split starts only when what is in force now is exactly what the lock says, and never with a policy that is provisional: a threshold changed after the
// calibration, a new weight file, another aggregation, another manifest each make it refuse, naming the member that differs and never its value.
// PRIVACY: a lock holds versions, hashes, thresholds and counts: nothing of a person.

public sealed record QualificationWeightHashes(string FaceDetector, string AntiSpoofing, string FaceRecognition);

public sealed record QualificationAggregation(string MatchRule, int MatchFrames, string LivenessRule, int LivenessFrames);

public sealed record QualificationFrozenThresholds(double Accept, double ReviewFloor, double MinEachFrame, double LivenessMinScore);

/// <summary>
/// What a qualification froze before its final test (<c>lock.json</c>): <see cref="PolicyVersion"/> and <see cref="PolicyCanonicalHash"/> (the canonical hash of the policy file, E70/F01/US02/T01),
/// <see cref="WeightsHash"/> (the fingerprint of the three weight hashes, E70/F01/US02/T01) and <see cref="Weights"/> (the three of them), the <see cref="Candidates"/> loaded (role and
/// weight hash), the <see cref="Aggregation"/>, the <see cref="Thresholds"/>, the <see cref="DatasetManifestHash"/> (the canonical SHA-256 of the manifest file), how many
/// <see cref="TestPersons"/> the test split has, and <see cref="CreatedAt"/>. <see cref="PolicyProvisional"/> is true only in a configuration that cannot be frozen: a lock is never written
/// from a provisional policy, and a provisional one never passes <see cref="Verify"/>.
/// </summary>
public sealed record QualificationLock(
    string PolicyVersion,
    string PolicyCanonicalHash,
    bool PolicyProvisional,
    string WeightsHash,
    QualificationWeightHashes Weights,
    IReadOnlyDictionary<string, string> Candidates,
    QualificationAggregation Aggregation,
    QualificationFrozenThresholds Thresholds,
    string DatasetManifestHash,
    int TestPersons,
    DateTimeOffset CreatedAt)
{
    public const string FileName = "lock.json";

    /// <summary>The code of a final test that refuses to start because what is in force is not what was frozen.</summary>
    public const string MismatchCode = "qualification_lock_mismatch";

    private static readonly JsonSerializerOptions StrictJson = new(QualificationResults.JsonOptions)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>
    /// The lock of a configuration: the policy and its canonical hash, the weights the store proved (<paramref name="models"/> must be ready, with the three hashes) and the candidates it loaded,
    /// the dataset by the hash of its manifest and the persons of its test split. <see cref="PolicyProvisional"/> is the policy's own flag: a configuration that is provisional is described
    /// here as it is (so that the final test can name it), and refused where it matters (<see cref="Save"/>, <see cref="Verify"/>).
    /// </summary>
    /// <exception cref="QualificationRunException"><see cref="QualificationRunCodes.LockNotFreezable"/>: the models are not ready, or a hash is not a SHA-256.</exception>
    public static QualificationLock From(
        IdentityDecisionPolicy policy,
        string policyCanonicalHash,
        BiometricModelsSnapshot models,
        string datasetManifestHash,
        int testPersons,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(models);
        if (!IsSha256(policyCanonicalHash) || !IsSha256(datasetManifestHash) || testPersons < 0)
            throw new QualificationRunException(QualificationRunCodes.LockNotFreezable, "hash_or_count_invalid");

        var weightsHash = IdentityConfigurationFingerprint.ComputeWeightsHash(models) ?? throw new QualificationRunException(QualificationRunCodes.LockNotFreezable, "models_not_ready");
        var thresholds = QualificationThresholds.From(policy);
        var candidates = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in models.Candidates.Where(info => info.Loaded && info.WeightHash is { } hash && IsSha256(hash)))
            candidates[candidate.Role] = candidate.WeightHash!.ToLowerInvariant();

        return new QualificationLock(
            policy.Version,
            policyCanonicalHash.ToLowerInvariant(),
            policy.Provisional,
            weightsHash,
            new QualificationWeightHashes(WeightOf(models, BiometricModelRoles.FaceDetector), WeightOf(models, BiometricModelRoles.AntiSpoofing), WeightOf(models, BiometricModelRoles.FaceRecognition)),
            new Dictionary<string, string>(candidates, StringComparer.Ordinal),
            new QualificationAggregation(thresholds.MatchAggregation, thresholds.MatchFrames, thresholds.LivenessRule, thresholds.LivenessFrames),
            new QualificationFrozenThresholds(thresholds.Accept, thresholds.ReviewFloor, thresholds.MinEachFrame, thresholds.LivenessMinScore),
            datasetManifestHash.ToLowerInvariant(),
            testPersons,
            createdAt);
    }

    public static QualificationLock FromResults(QualificationResults results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var roles = new[] { BiometricModelRoles.FaceDetector, BiometricModelRoles.AntiSpoofing, BiometricModelRoles.FaceRecognition };
        BiometricModelInfo Info(QualificationModelInfo model) => new(model.Role, model.Name, model.Version, model.License, model.Provenance, model.WeightSha256, model.Loaded, model.FailureCode);
        var snapshot = new BiometricModelsSnapshot(BiometricModelsState.Ready, null, [.. results.Models.Where(model => roles.Contains(model.Role, StringComparer.Ordinal)).Select(Info)])
        {
            Candidates = [.. results.Models.Where(model => !roles.Contains(model.Role, StringComparer.Ordinal)).Select(Info)],
        };
        var thresholds = results.Thresholds;
        var weightsHash = IdentityConfigurationFingerprint.ComputeWeightsHash(snapshot);
        var candidates = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in snapshot.Candidates.Where(info => info.Loaded && info.WeightHash is { } hash && IsSha256(hash)))
            candidates[candidate.Role] = candidate.WeightHash!.ToLowerInvariant();

        // A run whose models were not all loaded has no weights hash: it is described with what it has, so that every member of it differs from a frozen lock and the report says so.
        string Weight(string role) => snapshot.Models.FirstOrDefault(info => info.Role == role)?.WeightHash?.ToLowerInvariant() ?? string.Empty;
        return new QualificationLock(
            results.Policy.Version,
            results.Policy.CanonicalHash.ToLowerInvariant(),
            results.Policy.Provisional,
            weightsHash ?? string.Empty,
            new QualificationWeightHashes(Weight(BiometricModelRoles.FaceDetector), Weight(BiometricModelRoles.AntiSpoofing), Weight(BiometricModelRoles.FaceRecognition)),
            new Dictionary<string, string>(candidates, StringComparer.Ordinal),
            new QualificationAggregation(thresholds.MatchAggregation, thresholds.MatchFrames, thresholds.LivenessRule, thresholds.LivenessFrames),
            new QualificationFrozenThresholds(thresholds.Accept, thresholds.ReviewFloor, thresholds.MinEachFrame, thresholds.LivenessMinScore),
            results.DatasetManifestHash.ToLowerInvariant(),
            results.Dataset.Splits.FirstOrDefault(split => string.Equals(split.Split, QualificationSplits.Test, StringComparison.Ordinal))?.Persons ?? 0,
            results.RunAt);
    }

    public static QualificationLock Capture(QualificationPipeline pipeline, string datasetManifestHash, int testPersons, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var snapshot = pipeline.PolicySnapshot;
        if (!snapshot.IsReady)
            throw new QualificationRunException(QualificationRunCodes.LockNotFreezable, snapshot.ReasonCode);

        return From(snapshot.Policy, snapshot.Hash, pipeline.Models.Snapshot, datasetManifestHash, testPersons, createdAt);
    }

    public static (string ManifestHash, int TestPersons) DatasetIdentityOf(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var (manifest, hash) = QualificationRunner.ReadManifest(Path.GetFullPath(manifestPath));
        return (hash, manifest.Persons?.Count(person => string.Equals(person.Split, QualificationSplits.Test, StringComparison.Ordinal)) ?? 0);
    }

    /// <summary>
    /// What differs between this lock (the frozen one) and <paramref name="current"/> (the configuration in force now): the NAMES of the members, never their values
    /// (<c>policyCanonicalHash</c>, <c>thresholds.accept</c>, <c>weights.faceRecognition</c>, <c>candidates.face_recognition_candidate</c>, <c>aggregation.matchRule</c>,
    /// <c>datasetManifestHash</c>, ...). The day the lock was made does not count. A policy that is provisional, in the lock or in force, is always a difference
    /// (<c>policyProvisional</c>): a provisional policy is never qualified.
    /// </summary>
    public QualificationLockVerdict Verify(QualificationLock current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var differing = new List<string>();
        void Text(string member, string saved, string now, StringComparison comparison)
        {
            if (!string.Equals(saved, now, comparison))
                differing.Add(member);
        }

        void Number(string member, double saved, double now)
        {
            if (saved != now)
                differing.Add(member);
        }

        Text("policyVersion", PolicyVersion, current.PolicyVersion, StringComparison.Ordinal);
        Text("policyCanonicalHash", PolicyCanonicalHash, current.PolicyCanonicalHash, StringComparison.OrdinalIgnoreCase);
        if (PolicyProvisional || current.PolicyProvisional)
            differing.Add("policyProvisional");

        Text("weightsHash", WeightsHash, current.WeightsHash, StringComparison.OrdinalIgnoreCase);
        Text("weights.faceDetector", Weights.FaceDetector, current.Weights.FaceDetector, StringComparison.OrdinalIgnoreCase);
        Text("weights.antiSpoofing", Weights.AntiSpoofing, current.Weights.AntiSpoofing, StringComparison.OrdinalIgnoreCase);
        Text("weights.faceRecognition", Weights.FaceRecognition, current.Weights.FaceRecognition, StringComparison.OrdinalIgnoreCase);
        foreach (var role in Candidates.Keys.Concat(current.Candidates.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var frozenHas = Candidates.TryGetValue(role, out var frozenHash);
            var currentHas = current.Candidates.TryGetValue(role, out var currentHash);
            if (frozenHas != currentHas || !string.Equals(frozenHash, currentHash, StringComparison.OrdinalIgnoreCase))
                differing.Add("candidates." + role);
        }

        Text("aggregation.matchRule", Aggregation.MatchRule, current.Aggregation.MatchRule, StringComparison.Ordinal);
        Number("aggregation.matchFrames", Aggregation.MatchFrames, current.Aggregation.MatchFrames);
        Text("aggregation.livenessRule", Aggregation.LivenessRule, current.Aggregation.LivenessRule, StringComparison.Ordinal);
        Number("aggregation.livenessFrames", Aggregation.LivenessFrames, current.Aggregation.LivenessFrames);
        Number("thresholds.accept", Thresholds.Accept, current.Thresholds.Accept);
        Number("thresholds.reviewFloor", Thresholds.ReviewFloor, current.Thresholds.ReviewFloor);
        Number("thresholds.minEachFrame", Thresholds.MinEachFrame, current.Thresholds.MinEachFrame);
        Number("thresholds.livenessMinScore", Thresholds.LivenessMinScore, current.Thresholds.LivenessMinScore);
        Text("datasetManifestHash", DatasetManifestHash, current.DatasetManifestHash, StringComparison.OrdinalIgnoreCase);
        Number("testPersons", TestPersons, current.TestPersons);
        return new QualificationLockVerdict(differing);
    }

    public string ToJson() => JsonSerializer.Serialize(this, QualificationResults.JsonOptions);

    /// <summary>
    /// Writes the lock to <paramref name="path"/>. A lock is frozen once: a file that is there already is never replaced (a new version of the weights or of the thresholds is a new
    /// qualification, with its own folder), and a provisional policy is never frozen.
    /// </summary>
    /// <exception cref="QualificationRunException"><see cref="QualificationRunCodes.LockNotFreezable"/>: the policy is provisional, or a lock is there already.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (PolicyProvisional)
            throw new QualificationRunException(QualificationRunCodes.LockNotFreezable, "policy_provisional");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(ToJson());
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new QualificationRunException(QualificationRunCodes.LockNotFreezable, "lock_exists");
        }
    }

    public static QualificationLock Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        QualificationLock? frozen;
        try
        {
            frozen = JsonSerializer.Deserialize<QualificationLock>(File.ReadAllText(path), StrictJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new QualificationRunException(QualificationRunCodes.LockUnreadable);
        }

        if (frozen is null || !frozen.IsWhole())
            throw new QualificationRunException(QualificationRunCodes.LockUnreadable);

        return frozen;
    }

    private bool IsWhole()
    {
        static bool Threshold(double value) => double.IsFinite(value) && value > 0 && value <= 1;
        return !string.IsNullOrWhiteSpace(PolicyVersion)
            && IsSha256(PolicyCanonicalHash)
            && IsSha256(WeightsHash)
            && IsSha256(Weights.FaceDetector)
            && IsSha256(Weights.AntiSpoofing)
            && IsSha256(Weights.FaceRecognition)
            && Candidates.Values.All(IsSha256)
            && !string.IsNullOrWhiteSpace(Aggregation.MatchRule)
            && !string.IsNullOrWhiteSpace(Aggregation.LivenessRule)
            && Aggregation.MatchFrames > 0
            && Aggregation.LivenessFrames > 0
            && Threshold(Thresholds.Accept)
            && Threshold(Thresholds.ReviewFloor)
            && Threshold(Thresholds.MinEachFrame)
            && Threshold(Thresholds.LivenessMinScore)
            && IsSha256(DatasetManifestHash)
            && TestPersons >= 0;
    }

    private static string WeightOf(BiometricModelsSnapshot models, string role)
    {
        return models.Models.First(info => string.Equals(info.Role, role, StringComparison.Ordinal)).WeightHash!.ToLowerInvariant();
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record QualificationLockVerdict(IReadOnlyList<string> DifferingMembers)
{
    public bool Ok => DifferingMembers.Count == 0;

    public string? Code => Ok ? null : QualificationLock.MismatchCode;

    public void ThrowIfMismatch()
    {
        if (!Ok)
            throw new QualificationLockMismatchException(DifferingMembers);
    }
}

public sealed class QualificationLockMismatchException(IReadOnlyList<string> members) : Exception(QualificationLock.MismatchCode + ": " + string.Join(", ", members))
{
    public string Code => QualificationLock.MismatchCode;

    public IReadOnlyList<string> Members { get; } = members;
}

/// <summary>
/// The gate of the final test (NF-198): it starts the test only when the configuration in force is the one that was frozen, the policy is calibrated, the test split can measure every kind of
/// critical case, no report of a final test is in the output folder and the final test was not started with this lock before, and then starts it ONCE. A run that is refused is never
/// started: nothing of the test split is read.
/// <para>
/// <b>Once means once.</b> Before the run reads anything of the test split the gate writes a marker next to the lock (<see cref="MarkerPathOf"/>), with <c>FileMode.CreateNew</c>, and a marker
/// that is there refuses the test (<see cref="QualificationRunCodes.FinalAlreadyRun"/>) whatever the output folder is, whether the report was deleted, and whether the run before it broke:
/// a final test that is run again on the same lock after its numbers were seen is a tuning on the test split. A new qualification has a new lock and so a new marker.
/// </para>
/// </summary>
public static class QualificationFinalGate
{
    public const string MarkerSuffix = ".final-started";

    public static string MarkerPathOf(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        return Path.GetFullPath(lockPath) + MarkerSuffix;
    }

    /// <summary>
    /// Verifies <paramref name="current"/> against <paramref name="frozen"/> (<see cref="QualificationLockMismatchException"/> with the names of the members that differ, a provisional
    /// policy included), refuses a test split that has none of a kind of critical case (<paramref name="criticalKindsMissing"/>,
    /// <see cref="QualificationRunCodes.CriticalKindsMissing"/>: the criterion could not be measured and the test is made once), refuses an output folder that holds the report of a final test
    /// already and a lock whose marker is there (<see cref="QualificationRunCodes.FinalAlreadyRun"/>), writes the marker, and only then calls <paramref name="run"/>, once.
    /// </summary>
    public static async Task<T> RunOnceAsync<T>(
        QualificationLock frozen,
        QualificationLock current,
        string outDirectory,
        string lockPath,
        Func<Task<T>> run,
        IReadOnlyList<string>? criticalKindsMissing = null)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentNullException.ThrowIfNull(run);

        frozen.Verify(current).ThrowIfMismatch();
        if (criticalKindsMissing is { Count: > 0 })
            throw new QualificationRunException(QualificationRunCodes.CriticalKindsMissing, string.Join(", ", criticalKindsMissing));

        if (CandidateVersionReport.ExistsIn(outDirectory))
            throw new QualificationRunException(QualificationRunCodes.FinalAlreadyRun);

        WriteMarker(MarkerPathOf(lockPath), frozen);
        return await run();
    }

    private static void WriteMarker(string path, QualificationLock frozen)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(
                JsonSerializer.Serialize(
                    new
                    {
                        startedAt = DateTimeOffset.UtcNow,
                        policyCanonicalHash = frozen.PolicyCanonicalHash,
                        weightsHash = frozen.WeightsHash,
                        datasetManifestHash = frozen.DatasetManifestHash,
                    },
                    QualificationResults.JsonOptions));
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new QualificationRunException(QualificationRunCodes.FinalAlreadyRun);
        }
    }
}
