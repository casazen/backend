using Casazen.Core.Features;
using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Casazen.Infrastructure.Services;

public enum BiometricModelsState
{
    NotLoaded,

    Disabled,

    Loading,

    Ready,

    Unavailable,
}

public sealed record BiometricModelInfo(
    string Role,
    string Name,
    string Version,
    string License,
    string Provenance,
    string? WeightHash,
    bool Loaded,
    string? FailureCode);

public sealed record BiometricModelsSnapshot(BiometricModelsState State, string? ReasonCode, IReadOnlyList<BiometricModelInfo> Models)
{
    /// <summary>
    /// What is known of the two optional candidates (<see cref="BiometricModelRoles.AntiSpoofingCandidate"/> and <see cref="BiometricModelRoles.FaceRecognitionCandidate"/>, E72/F01/US02/T03), kept
    /// <b>apart from <see cref="Models"/></b> so that nothing that counts or enumerates the three models of the decisions can see them: a candidate that is not configured has the failure code
    /// <c>not_configured</c>, one that failed its code (<c>hash_mismatch</c>, <c>file_missing</c>, ...). Empty until the three models are ready and the candidates were looked at.
    /// </summary>
    public IReadOnlyList<BiometricModelInfo> Candidates { get; init; } = [];

    public bool IsReady => State == BiometricModelsState.Ready;
}

public sealed class LoadedBiometricModel : IDisposable
{
    internal LoadedBiometricModel(InferenceSession session, ModelFileOptions spec, string weightHash)
    {
        Session = session;
        Name = spec.Name;
        Version = spec.Version;
        License = spec.License;
        Provenance = spec.Provenance;
        WeightHash = weightHash;
        InputName = session.InputNames[0];
        var dimensions = session.InputMetadata[InputName].Dimensions;
        InputHeight = dimensions.Length == 4 && dimensions[2] > 0 ? dimensions[2] : 0;
        InputWidth = dimensions.Length == 4 && dimensions[3] > 0 ? dimensions[3] : 0;
    }

    public string Name { get; }

    public string Version { get; }

    public string License { get; }

    public string Provenance { get; }

    public string WeightHash { get; }

    internal InferenceSession Session { get; }

    internal string InputName { get; }

    internal int InputHeight { get; }

    internal int InputWidth { get; }

    public void Dispose() => Session.Dispose();
}

public interface IBiometricModelStore
{
    BiometricModelsSnapshot Snapshot { get; }

    bool IsAvailable { get; }

    /// <summary>YuNet: faces and landmarks. Null unless <see cref="IsAvailable"/>.</summary>
    LoadedBiometricModel? FaceDetector { get; }

    /// <summary>MiniFASNet: photo-of-photo and screen attacks. Null unless <see cref="IsAvailable"/>.</summary>
    LoadedBiometricModel? AntiSpoofing { get; }

    /// <summary>AuraFace-v1 (or SFace): the vector of a face. Null unless <see cref="IsAvailable"/>.</summary>
    LoadedBiometricModel? FaceRecognition { get; }

    /// <summary>
    /// The optional second candidate for the anti-spoofing model (E72/F01/US02/T03): null unless it is configured, verified by its SHA-256 and loaded. <b>Used only by the offline comparison of
    /// the qualification, never by a decision</b>; the readiness of the store, its health and the weights hash a decision is bound to ignore it. A default member, so that the stores of the tests
    /// need not know the candidates.
    /// </summary>
    LoadedBiometricModel? AntiSpoofingCandidate => null;

    LoadedBiometricModel? FaceRecognitionCandidate => null;
}

public sealed class BiometricUnavailableException() : InvalidOperationException("The biometric models are not available.");

/// <summary>
/// Loads the three models once, in the background, at startup of an environment where <c>Features:CheckInIdentityVerification</c> is on
/// (E03/F04/US01/T01, ADR-013-R8 and R14). The small models ship with the application; the large recognition model is external and downloaded.
/// <b>Every file is used only when its SHA-256 equals the pinned one</b>: a missing file, a missing hash, a different one, an invalid model or a
/// configuration error leaves the store <see cref="BiometricModelsState.Unavailable"/>, and the verification endpoints answer with the operator
/// path (the safe default is never "live" or "match"). The provenance of each model (name, version, licence, training-data provenance, weight
/// hash) is logged. It does not delay the start of the application.
/// </summary>
public sealed class BiometricModelStore(
    IOptions<BiometricModelOptions> options,
    IFeatureFlags featureFlags,
    ILogger<BiometricModelStore> logger,
    IHttpClientFactory? httpClientFactory = null) : BackgroundService, IBiometricModelStore
{
    public const string HttpClientName = "biometric-models";

    private static readonly string[] YuNetOutputs =
    [
        "cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32",
    ];

    private volatile Published _published = new(new BiometricModelsSnapshot(BiometricModelsState.NotLoaded, null, []), null, null, null);

    /// <summary>The folder relative model paths start from; the application folder unless a test says otherwise.</summary>
    internal string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public BiometricModelsSnapshot Snapshot => _published.Snapshot;

    public bool IsAvailable => _published.Snapshot.IsReady;

    public LoadedBiometricModel? FaceDetector => _published.Detector;

    public LoadedBiometricModel? AntiSpoofing => _published.AntiSpoofing;

    public LoadedBiometricModel? FaceRecognition => _published.Recognition;

    public LoadedBiometricModel? AntiSpoofingCandidate => _published.AntiSpoofingCandidate;

    public LoadedBiometricModel? FaceRecognitionCandidate => _published.RecognitionCandidate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        if (!featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification))
        {
            Publish(BiometricModelsState.Disabled, null, [], null, null, null);
            logger.LogInformation("Biometric models are not loaded: Features:{Flag} is off", FeatureFlags.CheckInIdentityVerification);
            return;
        }

        try
        {
            await LoadAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Publish(BiometricModelsState.Unavailable, "load_failed", [], null, null, null);
            logger.LogError("Biometric models could not be loaded ({ErrorType}); the verification goes to the operator", ex.GetType().Name);
        }
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Publish(BiometricModelsState.Loading, null, [], null, null, null);

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                logger.LogWarning("Biometrics configuration: {Problem}", problem);

            Publish(BiometricModelsState.Unavailable, "config_invalid", [], null, null, null);
            return;
        }

        Func<HttpClient>? http = httpClientFactory is null ? null : () => httpClientFactory.CreateClient(HttpClientName);
        var infos = new List<BiometricModelInfo>();
        var detector = await LoadOneAsync(BiometricModelRoles.FaceDetector, settings.FaceDetector, settings, http, infos, ValidateDetector, cancellationToken);
        var antiSpoofing = await LoadOneAsync(BiometricModelRoles.AntiSpoofing, settings.AntiSpoofing, settings, http, infos, ValidateSingleOutput, cancellationToken);
        var recognition = await LoadOneAsync(BiometricModelRoles.FaceRecognition, settings.FaceRecognition, settings, http, infos, ValidateSingleOutput, cancellationToken);

        if (detector is null || antiSpoofing is null || recognition is null)
        {
            detector?.Dispose();
            antiSpoofing?.Dispose();
            recognition?.Dispose();
            var reason = infos.First(i => !i.Loaded).FailureCode;
            Publish(BiometricModelsState.Unavailable, reason, infos, null, null, null);
            logger.LogWarning("Biometric models are not available ({Reason}); the verification goes to the operator", reason);
            return;
        }

        Publish(BiometricModelsState.Ready, null, infos, detector, antiSpoofing, recognition);
        logger.LogInformation("Biometric models are ready");

        await LoadCandidatesAsync(settings, http, cancellationToken);
    }

    public override void Dispose()
    {
        var published = _published;
        published.Detector?.Dispose();
        published.AntiSpoofing?.Dispose();
        published.Recognition?.Dispose();
        published.AntiSpoofingCandidate?.Dispose();
        published.RecognitionCandidate?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Verifies and loads the two optional candidates, after the three models of the decisions are ready (E72/F01/US02/T03, decision D6). A candidate with no path, no hash and no address is
    /// <c>not_configured</c> and nothing is read; one that is configured goes through the proof of the three models (its SHA-256 must be the pinned one: <c>hash_mismatch</c>,
    /// <c>file_missing</c>, <c>hash_not_configured</c>, <c>path_invalid</c>, <c>download_failed</c>, <c>model_incompatible</c>, <c>load_failed</c>), and one whose settings are not valid
    /// (<see cref="BiometricModelOptions.ValidateAntiSpoofingCandidate"/>) is <c>config_invalid</c>. <b>Nothing a candidate does changes the state of the store, its reason or the three
    /// models:</b> a failure here is a code in <see cref="BiometricModelsSnapshot.Candidates"/> and a line in the log, never an exception and never <see cref="BiometricModelsState.Unavailable"/>.
    /// </summary>
    private async Task LoadCandidatesAsync(BiometricModelOptions settings, Func<HttpClient>? http, CancellationToken cancellationToken)
    {
        var infos = new List<BiometricModelInfo>(2);
        LoadedBiometricModel? antiSpoofingCandidate = null;
        LoadedBiometricModel? recognitionCandidate = null;
        try
        {
            antiSpoofingCandidate = await LoadCandidateAsync(
                BiometricModelRoles.AntiSpoofingCandidate, settings.AntiSpoofingCandidate, settings.ValidateAntiSpoofingCandidate(), settings, http, infos, cancellationToken);
            recognitionCandidate = await LoadCandidateAsync(
                BiometricModelRoles.FaceRecognitionCandidate, settings.FaceRecognitionCandidate, settings.ValidateFaceRecognitionCandidate(), settings, http, infos, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            antiSpoofingCandidate?.Dispose();
            recognitionCandidate?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("The optional biometric candidates could not all be loaded ({ErrorType}); the models of the decisions are not affected", ex.GetType().Name);
            foreach (var role in new[] { BiometricModelRoles.AntiSpoofingCandidate, BiometricModelRoles.FaceRecognitionCandidate })
            {
                if (!infos.Any(info => info.Role == role))
                    infos.Add(new BiometricModelInfo(role, string.Empty, string.Empty, string.Empty, string.Empty, null, false, "load_failed"));
            }
        }

        var current = _published;
        _published = current with
        {
            Snapshot = current.Snapshot with { Candidates = infos },
            AntiSpoofingCandidate = antiSpoofingCandidate,
            RecognitionCandidate = recognitionCandidate,
        };
    }

    private async Task<LoadedBiometricModel?> LoadCandidateAsync(
        string role,
        ModelFileOptions spec,
        IReadOnlyList<string> problems,
        BiometricModelOptions settings,
        Func<HttpClient>? http,
        List<BiometricModelInfo> infos,
        CancellationToken cancellationToken)
    {
        if (!BiometricModelOptions.IsCandidateConfigured(spec))
        {
            infos.Add(new BiometricModelInfo(role, spec.Name, spec.Version, spec.License, spec.Provenance, null, false, BiometricModelStatusStates.NotConfigured));
            return null;
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                logger.LogWarning("Biometrics candidate configuration: {Problem}", problem);

            infos.Add(new BiometricModelInfo(role, spec.Name, spec.Version, spec.License, spec.Provenance, null, false, "config_invalid"));
            return null;
        }

        return await LoadOneAsync(role, spec, settings, http, infos, ValidateSingleOutput, cancellationToken);
    }

    private async Task<LoadedBiometricModel?> LoadOneAsync(
        string role,
        ModelFileOptions spec,
        BiometricModelOptions settings,
        Func<HttpClient>? http,
        List<BiometricModelInfo> infos,
        Func<InferenceSession, bool> isUsable,
        CancellationToken cancellationToken)
    {
        string? failure;
        ModelFileCheck check;
        using (var download = new CancellationTokenSource(TimeSpan.FromMinutes(settings.DownloadTimeoutMinutes)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, download.Token))
        {
            try
            {
                check = await ModelFileProvisioner.EnsureAsync(spec, BaseDirectory, http, logger, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                check = new ModelFileCheck(null, ModelFileProvisioner.DownloadFailed, null);
            }
        }

        failure = check.Failure;
        if (check.IsVerified)
        {
            try
            {
                using var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                if (settings.IntraOpThreads > 0)
                    sessionOptions.IntraOpNumThreads = settings.IntraOpThreads;

                var session = new InferenceSession(check.Path!, sessionOptions);
                if (isUsable(session))
                {
                    var model = new LoadedBiometricModel(session, spec, check.Sha256!);
                    infos.Add(new BiometricModelInfo(role, spec.Name, spec.Version, spec.License, spec.Provenance, check.Sha256, true, null));
                    logger.LogInformation(
                        "Biometric model loaded: role={Role} name={Name} version={Version} license={License} provenance={Provenance} weightSha256={WeightHash}",
                        role, spec.Name, spec.Version, spec.License, spec.Provenance, check.Sha256);
                    return model;
                }

                session.Dispose();
                failure = "model_incompatible";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Biometric model {Role} could not be loaded ({ErrorType})", role, ex.GetType().Name);
                failure = "load_failed";
            }
        }

        infos.Add(new BiometricModelInfo(role, spec.Name, spec.Version, spec.License, spec.Provenance, check.Sha256, false, failure));
        logger.LogWarning("Biometric model not usable: role={Role} name={Name} reason={Reason}", role, spec.Name, failure);
        return null;
    }

    private static bool ValidateDetector(InferenceSession session) =>
        session.InputNames.Count == 1
        && session.InputMetadata[session.InputNames[0]].Dimensions.Length == 4
        && YuNetOutputs.All(name => session.OutputMetadata.ContainsKey(name));

    private static bool ValidateSingleOutput(InferenceSession session) =>
        session.InputNames.Count == 1
        && session.InputMetadata[session.InputNames[0]].Dimensions.Length == 4
        && session.OutputNames.Count >= 1;

    private void Publish(
        BiometricModelsState state,
        string? reason,
        IReadOnlyList<BiometricModelInfo> models,
        LoadedBiometricModel? detector,
        LoadedBiometricModel? antiSpoofing,
        LoadedBiometricModel? recognition) =>
        _published = new Published(new BiometricModelsSnapshot(state, reason, models), detector, antiSpoofing, recognition);

    private sealed record Published(
        BiometricModelsSnapshot Snapshot,
        LoadedBiometricModel? Detector,
        LoadedBiometricModel? AntiSpoofing,
        LoadedBiometricModel? Recognition,
        LoadedBiometricModel? AntiSpoofingCandidate = null,
        LoadedBiometricModel? RecognitionCandidate = null);

    internal static IReadOnlyList<string> RequiredDetectorOutputs => YuNetOutputs;
}

public static class BiometricModelRoles
{
    public const string FaceDetector = "face_detector";

    public const string AntiSpoofing = "anti_spoofing";

    public const string FaceRecognition = "face_recognition";

    public const string MrzOcr = "mrz_ocr";

    public const string AntiSpoofingCandidate = "anti_spoofing_candidate";

    public const string FaceRecognitionCandidate = "face_recognition_candidate";
}

public static class BiometricModelStatusStates
{
    public const string Disabled = "disabled";

    public const string Loading = "loading";

    public const string Ready = "ready";

    public const string Present = "present";

    public const string Failed = "failed";

    public const string Unavailable = "unavailable";

    public const string NotConfigured = "not_configured";
}

public sealed record BiometricModelStatus(
    string Role,
    string Name,
    string Version,
    string License,
    string State,
    string? Code,
    bool Pinned)
{
    public bool IsUsable => State is BiometricModelStatusStates.Ready or BiometricModelStatusStates.Present;
}

public sealed record BiometricModelsStatus(
    bool FeatureEnabled,
    string State,
    string? ReasonCode,
    IReadOnlyList<BiometricModelStatus> Models)
{
    public IReadOnlyList<BiometricModelStatus> Candidates { get; init; } = [];
}

public static class BiometricModelStatuses
{
    private const string Unknown = "unknown";

    public static BiometricModelsStatus Describe(
        bool featureEnabled,
        BiometricModelsSnapshot snapshot,
        BiometricModelOptions biometrics,
        MrzOcrOptions mrzOcr,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(biometrics);
        ArgumentNullException.ThrowIfNull(mrzOcr);

        var models = new List<BiometricModelStatus>
        {
            OfStoreModel(BiometricModelRoles.FaceDetector, biometrics.FaceDetector, featureEnabled, snapshot),
            OfStoreModel(BiometricModelRoles.AntiSpoofing, biometrics.AntiSpoofing, featureEnabled, snapshot),
            OfStoreModel(BiometricModelRoles.FaceRecognition, biometrics.FaceRecognition, featureEnabled, snapshot),
            OfOcrModel(mrzOcr.Mrz, featureEnabled, baseDirectory ?? AppContext.BaseDirectory),
        };

        string state;
        if (!featureEnabled)
            state = BiometricModelStatusStates.Disabled;
        else if (models.All(model => model.IsUsable))
            state = BiometricModelStatusStates.Ready;
        else if (models.Any(model => model.State == BiometricModelStatusStates.Failed))
            state = BiometricModelStatusStates.Unavailable;
        else
            state = BiometricModelStatusStates.Loading;

        return new BiometricModelsStatus(
            featureEnabled,
            state,
            state == BiometricModelStatusStates.Unavailable ? snapshot.ReasonCode : null,
            models)
        {
            Candidates =
            [
                OfCandidate(BiometricModelRoles.AntiSpoofingCandidate, biometrics.AntiSpoofingCandidate, featureEnabled, snapshot),
                OfCandidate(BiometricModelRoles.FaceRecognitionCandidate, biometrics.FaceRecognitionCandidate, featureEnabled, snapshot),
            ],
        };
    }

    /// <summary>
    /// What is known of an optional candidate (E72/F01/US02/T03): off with the feature; <c>not_configured</c> when it has no path, no hash and no address (read from the options, so it is true
    /// before the store has finished and when it never will); then what the store found (<c>ready</c>, or <c>failed</c> with its code); <c>loading</c> while the store has not looked at it yet,
    /// and <c>failed</c> with <c>not_loaded</c> when it never will (the three models are not ready, or the flag was switched on after the start).
    /// </summary>
    private static BiometricModelStatus OfCandidate(string role, ModelFileOptions spec, bool featureEnabled, BiometricModelsSnapshot snapshot)
    {
        if (!featureEnabled)
            return Of(role, spec, BiometricModelStatusStates.Disabled, null);

        if (!BiometricModelOptions.IsCandidateConfigured(spec))
            return Of(role, spec, BiometricModelStatusStates.NotConfigured, null);

        var info = snapshot.Candidates.FirstOrDefault(candidate => string.Equals(candidate.Role, role, StringComparison.Ordinal));
        if (info is not null)
        {
            return info.Loaded
                ? Of(role, spec, BiometricModelStatusStates.Ready, null)
                : Of(role, spec, BiometricModelStatusStates.Failed, info.FailureCode ?? Unknown);
        }

        return snapshot.State switch
        {
            BiometricModelsState.NotLoaded or BiometricModelsState.Loading or BiometricModelsState.Ready => Of(role, spec, BiometricModelStatusStates.Loading, null),
            _ => Of(role, spec, BiometricModelStatusStates.Failed, "not_loaded"),
        };
    }

    private static BiometricModelStatus OfStoreModel(string role, ModelFileOptions spec, bool featureEnabled, BiometricModelsSnapshot snapshot)
    {
        if (!featureEnabled)
            return Of(role, spec, BiometricModelStatusStates.Disabled, null);

        var info = snapshot.Models.FirstOrDefault(model => string.Equals(model.Role, role, StringComparison.Ordinal));
        if (info is not null)
        {
            return info.Loaded
                ? Of(role, spec, BiometricModelStatusStates.Ready, null)
                : Of(role, spec, BiometricModelStatusStates.Failed, info.FailureCode ?? Unknown);
        }

        return snapshot.State switch
        {
            BiometricModelsState.NotLoaded or BiometricModelsState.Loading => Of(role, spec, BiometricModelStatusStates.Loading, null),

            BiometricModelsState.Unavailable => Of(role, spec, BiometricModelStatusStates.Failed, snapshot.ReasonCode ?? Unknown),

            BiometricModelsState.Disabled => Of(role, spec, BiometricModelStatusStates.Failed, "not_loaded"),

            _ => Of(role, spec, BiometricModelStatusStates.Failed, Unknown),
        };
    }

    private static BiometricModelStatus OfOcrModel(ModelFileOptions spec, bool featureEnabled, string baseDirectory)
    {
        if (!featureEnabled)
            return Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Disabled, null);

        if (!spec.HasWellFormedSha256())
            return Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Failed, ModelFileProvisioner.HashNotConfigured);

        if (string.IsNullOrWhiteSpace(spec.Path))
            return Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Failed, ModelFileProvisioner.PathInvalid);

        string path;
        try
        {
            path = System.IO.Path.IsPathRooted(spec.Path)
                ? spec.Path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, spec.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Failed, ModelFileProvisioner.PathInvalid);
        }

        return File.Exists(path)
            ? Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Present, null)
            : Of(BiometricModelRoles.MrzOcr, spec, BiometricModelStatusStates.Failed, ModelFileProvisioner.FileMissing);
    }

    private static BiometricModelStatus Of(string role, ModelFileOptions spec, string state, string? code) =>
        new(role, spec.Name, spec.Version, spec.License, state, code, spec.HasWellFormedSha256());
}

public interface IBiometricModelStatusReader
{
    BiometricModelsStatus Read();
}

public sealed class BiometricModelStatusReader(
    IBiometricModelStore store,
    IOptions<BiometricModelOptions> biometrics,
    IOptions<MrzOcrOptions> mrzOcr,
    IFeatureFlags featureFlags) : IBiometricModelStatusReader
{
    public BiometricModelsStatus Read() =>
        BiometricModelStatuses.Describe(
            featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification),
            store.Snapshot,
            biometrics.Value,
            mrzOcr.Value);
}
