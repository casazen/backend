using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12, ADR-013-R21): the REAL pipeline of the self-hosted identity verification, in this process, for the offline qualification. It is the services the
// application registers (AddSelfHostedIdentityVerification, and the registrations of the models and of the readers of the document that AddCasazenServices makes next to it),
// configured from the published appsettings.json that the test output carries like BundledCheckInModelTests, with the model files of MlModels: no double of a model, a reader or a
// policy. Nothing leaves the process: no HTTP client is registered (so the store can download nothing), no AI provider, no job. Nothing is logged: the services of the pipeline log
// codes and counts, and a qualification prints none of them.

/// <summary>How a <see cref="QualificationPipeline"/> is built; the defaults are the ones of a real run.</summary>
public sealed record QualificationPipelineOptions
{
    /// <summary>
    /// Whether environment variables are read on top of the published configuration, as the application reads them on Railway: how a candidate model is named
    /// (<c>Biometrics__FaceRecognitionCandidate__Path</c> and the others of its settings) and how a proposed policy file is tried before it is committed
    /// (<c>IdentityVerification__PolicyFile</c> and <c>IdentityVerification__PolicySha256</c>). The smoke test turns it off.
    /// </summary>
    public bool IncludeEnvironmentVariables { get; init; } = true;

    /// <summary>The time the models are given to load (verified by their SHA-256 and loaded): past it the run stops with <see cref="QualificationRunCodes.ModelsNotReady"/>.</summary>
    public TimeSpan ModelLoadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The clock of the run: its "today" in Rome is the day documents must still be valid on, and its instant stamps the results.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// Registrations made last, to replace what a machine cannot run (a test of the machine without Tesseract, say). A real run leaves it null: the pipeline of a qualification is the one of
    /// the application.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
}

/// <summary>
/// The real pipeline of the identity verification, built in-process for the qualification (E74): the geometry of the document and the side it shows, the fields read by zone and their
/// coherence, the frames of a sequence and their validation, the choice of the frames by the server, their liveness, the comparison with the portrait, the engine of the policy, and the
/// comparison of the optional candidate models. The services are the ones the application registers, read from the published configuration; waits for the models to be ready.
/// <para>
/// <b>The limits of time are widened, on purpose.</b> The time a model may take (<c>Biometrics:VerificationTimeoutMs</c>, <c>LivenessTimeoutMs</c>, <c>IdentityVerification:Presence:SequenceTimeoutMs</c>,
/// <c>MrzOcr:TimeoutSeconds</c>) is a limit of the live request: on a machine busy with a long run a genuine capture would end as a technical error and the rates would measure the machine.
/// They are raised to the most the settings allow (never lowered), and the time of every stage is measured and reported instead, so the p95 shows whether the live limit would hold.
/// </para>
/// </summary>
public sealed class QualificationPipeline : IAsyncDisposable
{
    /// <summary>The most the settings allow for the time of a model run (30 seconds).</summary>
    public const int WidenedModelTimeoutMs = 30_000;

    /// <summary>The most the settings allow for the time of the validation of a sequence (30 seconds).</summary>
    public const int WidenedSequenceTimeoutMs = 30_000;

    /// <summary>The most the settings allow for the time of the reading of a document (120 seconds).</summary>
    public const int WidenedOcrTimeoutSeconds = 120;

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private QualificationPipeline(ServiceProvider provider, AsyncServiceScope scope, TimeProvider clock)
    {
        _provider = provider;
        _scope = scope;
        Clock = clock;
        var services = scope.ServiceProvider;
        Models = services.GetRequiredService<IBiometricModelStore>();
        BiometricOptions = services.GetRequiredService<IOptions<BiometricModelOptions>>().Value;
        IdentityOptions = services.GetRequiredService<IOptions<IdentityVerificationOptions>>().Value;
        PolicyProvider = services.GetRequiredService<IdentityPolicyProvider>();
        Fingerprint = services.GetRequiredService<IdentityConfigurationFingerprint>();
        Formats = services.GetRequiredService<DocumentFormatRegistry>();
        Geometry = services.GetRequiredService<DocumentGeometryInspector>();
        Pictures = services.GetRequiredService<DocumentPictureAnalyzer>();
        Sides = services.GetRequiredService<DocumentSideClassifier>();
        ZonedFields = services.GetRequiredService<IZonedFieldReader>();
        Mrz = services.GetRequiredService<IMrzExtractionService>();
        CaptureQuality = services.GetRequiredService<CaptureQualityAssessor>();
        Coherence = services.GetRequiredService<DocumentCoherenceChecker>();
        FrameReader = services.GetRequiredService<FrameSequenceReader>();
        Validator = services.GetRequiredService<PresenceSequenceValidator>();
        Selector = services.GetRequiredService<PresenceFrameSelector>();
        Liveness = services.GetRequiredService<SequenceLivenessEvaluator>();
        Matcher = services.GetRequiredService<PresenceMatcher>();
        Engine = services.GetRequiredService<IdentityDecisionEngine>();
        CandidateComparison = services.GetRequiredService<IBiometricCandidateComparison>();
    }

    /// <summary>The clock of the run.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The models the store loaded (and the optional candidates).</summary>
    public IBiometricModelStore Models { get; }

    /// <summary>The settings of the models in force (the published ones, the time limits widened).</summary>
    public BiometricModelOptions BiometricOptions { get; }

    /// <summary>The settings of the identity verification in force (the published ones, the time of the sequence widened).</summary>
    public IdentityVerificationOptions IdentityOptions { get; }

    /// <summary>The provider of the versioned policy the application uses.</summary>
    public IdentityPolicyProvider PolicyProvider { get; }

    /// <summary>The fingerprint of the policy and the weights in force.</summary>
    public IdentityConfigurationFingerprint Fingerprint { get; }

    /// <summary>The registry of the formats of the documents.</summary>
    public DocumentFormatRegistry Formats { get; }

    /// <summary>The inspector of the geometry of a document: the card, its edges, its orientation, its portrait.</summary>
    public DocumentGeometryInspector Geometry { get; }

    /// <summary>The decoder of a picture of a document and its geometry.</summary>
    public DocumentPictureAnalyzer Pictures { get; }

    /// <summary>The classifier of the side a picture shows.</summary>
    public DocumentSideClassifier Sides { get; }

    /// <summary>The reader of the fields of a card by zone.</summary>
    public IZonedFieldReader ZonedFields { get; }

    /// <summary>The reader of the MRZ of a picture.</summary>
    public IMrzExtractionService Mrz { get; }

    /// <summary>The checks of the quality of a capture.</summary>
    public CaptureQualityAssessor CaptureQuality { get; }

    /// <summary>The coherence of the fields read on a document.</summary>
    public DocumentCoherenceChecker Coherence { get; }

    /// <summary>The intake of the frames of a sequence, with the limits of the application.</summary>
    public FrameSequenceReader FrameReader { get; }

    /// <summary>The validation of a sequence of frames.</summary>
    public PresenceSequenceValidator Validator { get; }

    /// <summary>The choice of the frames by the server.</summary>
    public PresenceFrameSelector Selector { get; }

    /// <summary>The liveness of the chosen frames.</summary>
    public SequenceLivenessEvaluator Liveness { get; }

    /// <summary>The comparison of the portrait of the document with the chosen frames.</summary>
    public PresenceMatcher Matcher { get; }

    /// <summary>The engine of the policy.</summary>
    public IdentityDecisionEngine Engine { get; }

    /// <summary>The comparison of the optional candidate models with the reference ones.</summary>
    public IBiometricCandidateComparison CandidateComparison { get; }

    /// <summary>The policy in force, read once; ready (the run does not start otherwise).</summary>
    public IdentityPolicySnapshot PolicySnapshot => PolicyProvider.Snapshot;

    /// <summary>The policy in force.</summary>
    public IdentityDecisionPolicy ActivePolicy => PolicySnapshot is { IsReady: true } snapshot
        ? snapshot.Policy
        : throw new QualificationRunException(QualificationRunCodes.PolicyNotReady, PolicySnapshot.ReasonCode);

    /// <summary>
    /// The folders that hold the committed policy files, which a calibration never writes to: the folder the application reads the policy from (next to the test assembly) and, when the tests run
    /// from a checkout, the folder of the sources (<c>Casazen.Web/IdentityPolicies</c>).
    /// </summary>
    public IReadOnlyList<string> ProtectedPolicyFolders()
    {
        var file = IdentityOptions.PolicyFile;
        var path = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, file));
        var folders = new List<string>();
        if (Path.GetDirectoryName(path) is { Length: > 0 } published)
            folders.Add(published);

        // The checkout the tests were built from: the folder of the solution is the first one above the test output that holds Casazen.sln.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
                continue;

            var sources = Path.Combine(directory.FullName, "Casazen.Web", "IdentityPolicies");
            if (Directory.Exists(sources))
                folders.Add(sources);

            break;
        }

        return folders;
    }

    /// <summary>True when a candidate of the recognition model is loaded.</summary>
    public bool HasRecognitionCandidate => Models.FaceRecognitionCandidate is not null;

    /// <summary>True when a candidate of the anti-spoofing model is loaded.</summary>
    public bool HasAntiSpoofingCandidate => Models.AntiSpoofingCandidate is not null;

    /// <summary>
    /// Builds the pipeline and waits for it: the models verified by their SHA-256 and loaded, the policy read and pinned, the formats loaded. Any of them that cannot be used stops the
    /// run with its code (<see cref="QualificationRunException"/>), never a partial pipeline.
    /// </summary>
    public static async Task<QualificationPipeline> CreateAsync(QualificationPipelineOptions? options = null, CancellationToken ct = default)
    {
        options ??= new QualificationPipelineOptions();
        var provider = Build(options);
        try
        {
            var store = provider.GetRequiredService<BiometricModelStore>();
            try
            {
                await store.LoadAsync(ct).WaitAsync(options.ModelLoadTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new QualificationRunException(QualificationRunCodes.ModelsNotReady);
            }

            if (!store.IsAvailable)
                throw new QualificationRunException(QualificationRunCodes.ModelsUnavailable, store.Snapshot.ReasonCode ?? "unknown");

            var invalid = provider.GetRequiredService<IOptions<IdentityVerificationOptions>>().Value.Validate();
            if (invalid.Count > 0)
                throw new QualificationRunException(QualificationRunCodes.ConfigurationInvalid, string.Join("; ", invalid));

            var policy = provider.GetRequiredService<IdentityPolicyProvider>().Snapshot;
            if (!policy.IsReady)
                throw new QualificationRunException(QualificationRunCodes.PolicyNotReady, policy.ReasonCode);

            if (provider.GetRequiredService<DocumentFormatRegistry>().All.Count == 0)
                throw new QualificationRunException(QualificationRunCodes.FormatsEmpty);

            return new QualificationPipeline(provider, provider.CreateAsyncScope(), options.Clock);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>The models of the run as the results report them: the three of the decisions, then the candidates that were looked at (loaded or not, with the code that says why not).</summary>
    public IReadOnlyList<QualificationModelInfo> ModelInfos()
    {
        var snapshot = Models.Snapshot;
        return
        [
            .. snapshot.Models.Concat(snapshot.Candidates).Select(model => new QualificationModelInfo(
                model.Role, model.Name, model.Version, model.License, model.Provenance, model.WeightHash, model.Loaded, model.FailureCode)),
        ];
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private static ServiceProvider Build(QualificationPipelineOptions options)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        if (options.IncludeEnvironmentVariables)
            configuration.AddEnvironmentVariables();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration.Build());
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton(options.Clock);
        services.AddSingleton<IFeatureFlags>(new QualificationFlags());

        // The models, as AddCasazenServices registers them. Without an HTTP client factory the store downloads nothing.
        services.AddOptions<BiometricModelOptions>().BindConfiguration(BiometricModelOptions.SectionName);
        services.AddSingleton<BiometricModelStore>();
        services.AddSingleton<IBiometricModelStore>(provider => provider.GetRequiredService<BiometricModelStore>());
        services.AddSingleton<IFaceDetector, FaceDetector>();
        services.AddSingleton<IFaceEmbedder, FaceEmbedder>();
        services.AddScoped<IAntiSpoofingService, AntiSpoofingService>();
        services.AddSingleton<CaptureQualityAssessor>();

        // The readers of the document, as AddCasazenServices registers them.
        services.AddOptions<MrzOcrOptions>().BindConfiguration(MrzOcrOptions.SectionName);
        services.AddSingleton<IMrzBandLocator, MrzImageLocator>();
        services.AddSingleton<TesseractMrzTextReader>();
        services.AddSingleton<IMrzTextReader>(provider => provider.GetRequiredService<TesseractMrzTextReader>());
        services.AddSingleton<TesseractPageTextReader>();
        services.AddSingleton<IPageTextReader>(provider => provider.GetRequiredService<TesseractPageTextReader>());
        services.AddScoped<IMrzExtractionService, MrzExtractionService>();

        // The self-hosted identity verification itself: its options and every hook of the plan.
        services.AddSelfHostedIdentityVerification();

        // The limits of time of the live request, raised and never lowered (see the remarks of the class).
        services.PostConfigure<BiometricModelOptions>(settings =>
        {
            settings.VerificationTimeoutMs = Math.Max(settings.VerificationTimeoutMs, WidenedModelTimeoutMs);
            settings.LivenessTimeoutMs = Math.Max(settings.LivenessTimeoutMs, WidenedModelTimeoutMs);
        });
        services.PostConfigure<IdentityVerificationOptions>(settings => settings.Presence.SequenceTimeoutMs = Math.Max(settings.Presence.SequenceTimeoutMs, WidenedSequenceTimeoutMs));
        services.PostConfigure<MrzOcrOptions>(settings => settings.TimeoutSeconds = Math.Max(settings.TimeoutSeconds, WidenedOcrTimeoutSeconds));

        options.ConfigureServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    /// <summary>The flags of the run: the self-hosted verification is on (it is what is qualified); the automatic approval and the evidence are not read by anything here.</summary>
    private sealed class QualificationFlags : IFeatureFlags
    {
        public bool IsEnabled(string flag)
        {
            return flag is FeatureFlags.CheckInIdentityVerification or FeatureFlags.CheckInIdentitySelfHosted;
        }
    }
}
