using Casazen.Core.Features;
using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Casazen.Infrastructure.Services;

/// <summary>Where the models stand: nothing is loaded before the first start, and only <see cref="Ready"/> lets the endpoints use them.</summary>
public enum BiometricModelsState
{
    /// <summary>Not started yet.</summary>
    NotLoaded,

    /// <summary>The feature is off in this environment: nothing is loaded or downloaded.</summary>
    Disabled,

    /// <summary>The files are being verified, downloaded or loaded.</summary>
    Loading,

    /// <summary>The three models are verified and loaded.</summary>
    Ready,

    /// <summary>At least one model cannot be used: the verification endpoints answer with the operator path.</summary>
    Unavailable,
}

/// <summary>What is known of one model: its provenance (ADR-013-R14) and whether it is loaded, or why not (a code).</summary>
public sealed record BiometricModelInfo(
    string Role,
    string Name,
    string Version,
    string License,
    string Provenance,
    string? WeightHash,
    bool Loaded,
    string? FailureCode);

/// <summary>A reading of the state of the store, for the log, the health of the service and the tests. Codes only.</summary>
public sealed record BiometricModelsSnapshot(BiometricModelsState State, string? ReasonCode, IReadOnlyList<BiometricModelInfo> Models)
{
    /// <summary>
    /// What is known of the two optional candidates (<see cref="BiometricModelRoles.AntiSpoofingCandidate"/> and <see cref="BiometricModelRoles.FaceRecognitionCandidate"/>, E72/F01/US02/T03), kept
    /// <b>apart from <see cref="Models"/></b> so that nothing that counts or enumerates the three models of the decisions can see them: a candidate that is not configured has the failure code
    /// <c>not_configured</c>, one that failed its code (<c>hash_mismatch</c>, <c>file_missing</c>, ...). Empty until the three models are ready and the candidates were looked at.
    /// </summary>
    public IReadOnlyList<BiometricModelInfo> Candidates { get; init; } = [];

    /// <summary>True only when the three models are verified and loaded. A candidate never takes part.</summary>
    public bool IsReady => State == BiometricModelsState.Ready;
}

/// <summary>A model file whose SHA-256 was proved, loaded in an ONNX Runtime session that is shared by every request.</summary>
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

    /// <summary>SHA-256 of the weights (uppercase hexadecimal), the one that was proved.</summary>
    public string WeightHash { get; }

    internal InferenceSession Session { get; }

    internal string InputName { get; }

    /// <summary>Height the model wants, 0 when it accepts any.</summary>
    internal int InputHeight { get; }

    /// <summary>Width the model wants, 0 when it accepts any.</summary>
    internal int InputWidth { get; }

    public void Dispose() => Session.Dispose();
}

/// <summary>The models of the face check, once, for the whole process (ADR-013-R8, R14).</summary>
public interface IBiometricModelStore
{
    BiometricModelsSnapshot Snapshot { get; }

    /// <summary>True only when the three models are verified and loaded.</summary>
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

    /// <summary>The optional second candidate for the recognition model (E72/F01/US02/T03): same rules as <see cref="AntiSpoofingCandidate"/>.</summary>
    LoadedBiometricModel? FaceRecognitionCandidate => null;
}

/// <summary>Thrown by the model wrappers when they are asked to run while the store is not ready: the caller answers with the operator path.</summary>
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
    /// <summary>Name of the HTTP client the large model is downloaded with.</summary>
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
        // The first synchronous part of ExecuteAsync runs inside StartAsync: yield, so hashing and loading never delay the start of the host.
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
            // The host is stopping.
        }
        catch (Exception ex)
        {
            Publish(BiometricModelsState.Unavailable, "load_failed", [], null, null, null);
            logger.LogError("Biometric models could not be loaded ({ErrorType}); the verification goes to the operator", ex.GetType().Name);
        }
    }

    /// <summary>Verifies and loads the three models. Public to the tests: the host calls it once from <see cref="ExecuteAsync"/>.</summary>
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

        // The optional candidates are looked at only now, with the three models already published: whatever they are, the store stays ready and the decisions do not wait for them.
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
            // The host is stopping: nothing of a candidate is kept.
            antiSpoofingCandidate?.Dispose();
            recognitionCandidate?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // The type only. The three models are published and stay so; what was loaded before the failure is kept, the rest is reported as failed.
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

        // The same proof, the same session and the same checks as the three models (one image input, an output to read).
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
                    // ADR-013-R14: the provenance of every model that is used, in the log of every start.
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

    /// <summary>YuNet: one image input and the twelve outputs the decoder reads by name.</summary>
    private static bool ValidateDetector(InferenceSession session) =>
        session.InputNames.Count == 1
        && session.InputMetadata[session.InputNames[0]].Dimensions.Length == 4
        && YuNetOutputs.All(name => session.OutputMetadata.ContainsKey(name));

    /// <summary>MiniFASNet and the recognition models: one image input and at least one output (the first is the one that is read).</summary>
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

    /// <summary>The state and the sessions, swapped in one assignment so a request never sees half of them. The candidates are null until they are loaded, after the three models.</summary>
    private sealed record Published(
        BiometricModelsSnapshot Snapshot,
        LoadedBiometricModel? Detector,
        LoadedBiometricModel? AntiSpoofing,
        LoadedBiometricModel? Recognition,
        LoadedBiometricModel? AntiSpoofingCandidate = null,
        LoadedBiometricModel? RecognitionCandidate = null);

    internal static IReadOnlyList<string> RequiredDetectorOutputs => YuNetOutputs;
}

/// <summary>
/// The roles of the models the identity verification needs: the three the store loads, the OCR-B file of the MRZ (E22/F01/US01/T01) and the two optional candidates of the second-candidate
/// slot (E72/F01/US02/T03), which are loaded after the three and never take part in a decision.
/// </summary>
public static class BiometricModelRoles
{
    /// <summary>YuNet: faces and landmarks.</summary>
    public const string FaceDetector = "face_detector";

    /// <summary>MiniFASNet: photo-of-photo and screen attacks.</summary>
    public const string AntiSpoofing = "anti_spoofing";

    /// <summary>AuraFace-v1 (or SFace): the vector of a face.</summary>
    public const string FaceRecognition = "face_recognition";

    /// <summary>OCR-B language data of Tesseract for the lines of the MRZ, loaded by the document readers and not by the store.</summary>
    public const string MrzOcr = "mrz_ocr";

    /// <summary>The optional second candidate for the anti-spoofing model (<c>Biometrics:AntiSpoofingCandidate</c>): a plug-in point for the offline comparison, never part of a decision.</summary>
    public const string AntiSpoofingCandidate = "anti_spoofing_candidate";

    /// <summary>The optional second candidate for the recognition model (<c>Biometrics:FaceRecognitionCandidate</c>): a plug-in point for the offline comparison, never part of a decision.</summary>
    public const string FaceRecognitionCandidate = "face_recognition_candidate";
}

/// <summary>The states of a model, and of the set of them, in <see cref="BiometricModelsStatus"/>: codes the admin console and the health check speak.</summary>
public static class BiometricModelStatusStates
{
    /// <summary><c>Features:CheckInIdentityVerification</c> is off: nothing is loaded and nothing is checked.</summary>
    public const string Disabled = "disabled";

    /// <summary>The models are being verified, downloaded or loaded (the start of the API).</summary>
    public const string Loading = "loading";

    /// <summary>Verified by its SHA-256 and loaded.</summary>
    public const string Ready = "ready";

    /// <summary>
    /// Pinned and on disk but not loaded by the store: the OCR-B file, whose hash the document readers verify the first time a document is read (the
    /// status never hashes a file: it runs on every health probe).
    /// </summary>
    public const string Present = "present";

    /// <summary>Cannot be used: <see cref="BiometricModelStatus.Code"/> says why (a code of <see cref="ModelFileProvisioner"/>).</summary>
    public const string Failed = "failed";

    /// <summary>Of the set only: at least one model cannot be used, so every verification goes to the operator.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Of an optional candidate only (E72/F01/US02/T03): it has no path, no hash and no address, so there is nothing to load and nothing is wrong.</summary>
    public const string NotConfigured = "not_configured";
}

/// <summary>
/// One model as the status shows it: what it is (name, version, licence), whether its SHA-256 is pinned, and where it stands, with the failure
/// code when it cannot be used. Never a path, a hash or an address.
/// </summary>
public sealed record BiometricModelStatus(
    string Role,
    string Name,
    string Version,
    string License,
    string State,
    string? Code,
    bool Pinned)
{
    /// <summary>The model can be used: loaded, or present and to be verified at its first use.</summary>
    public bool IsUsable => State is BiometricModelStatusStates.Ready or BiometricModelStatusStates.Present;
}

/// <summary>The status of the models of the identity verification: whether the feature is on, the state of the set, and one entry per model.</summary>
public sealed record BiometricModelsStatus(
    bool FeatureEnabled,
    string State,
    string? ReasonCode,
    IReadOnlyList<BiometricModelStatus> Models)
{
    /// <summary>
    /// The two optional candidates (E72/F01/US02/T03), listed apart from <see cref="Models"/>: <b>they never change <see cref="State"/>, <see cref="ReasonCode"/> or the health entry</b>, which
    /// are the ones of the three models and the OCR-B file. A candidate that is not configured is <see cref="BiometricModelStatusStates.NotConfigured"/>.
    /// </summary>
    public IReadOnlyList<BiometricModelStatus> Candidates { get; init; } = [];
}

/// <summary>
/// What is known of the models without touching their files: no hashing, no download, no network (it runs on every health probe). The state of
/// the three models of the store comes from its snapshot, the state of the OCR-B file from the configuration and the disk (pinned and found).
/// </summary>
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
            // Apart from the models above: they take no part in the state, the reason or the health.
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

            // The configuration is invalid or the loading failed as a whole: no model was tried one by one.
            BiometricModelsState.Unavailable => Of(role, spec, BiometricModelStatusStates.Failed, snapshot.ReasonCode ?? Unknown),

            // The flag is on now and was off when the process started: the models are loaded at start, a restart loads them.
            BiometricModelsState.Disabled => Of(role, spec, BiometricModelStatusStates.Failed, "not_loaded"),

            // Ready with no entry for this model cannot happen (the store publishes the three): never claim more than is known.
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

/// <summary>The status of the models as it is now: one reading for the health check and the admin console, so they cannot disagree.</summary>
public interface IBiometricModelStatusReader
{
    BiometricModelsStatus Read();
}

/// <inheritdoc cref="IBiometricModelStatusReader" />
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
