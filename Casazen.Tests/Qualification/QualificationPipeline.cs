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

public sealed record QualificationPipelineOptions
{
    /// <summary>
    /// Whether environment variables are read on top of the published configuration, as the application reads them on Railway: how a candidate model is named
    /// (<c>Biometrics__FaceRecognitionCandidate__Path</c> and the others of its settings) and how a proposed policy file is tried before it is committed
    /// (<c>IdentityVerification__PolicyFile</c> and <c>IdentityVerification__PolicySha256</c>). The smoke test turns it off.
    /// </summary>
    public bool IncludeEnvironmentVariables { get; init; } = true;

    public TimeSpan ModelLoadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

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
    public const int WidenedModelTimeoutMs = 30_000;

    public const int WidenedSequenceTimeoutMs = 30_000;

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

    public TimeProvider Clock { get; }

    public IBiometricModelStore Models { get; }

    public BiometricModelOptions BiometricOptions { get; }

    public IdentityVerificationOptions IdentityOptions { get; }

    public IdentityPolicyProvider PolicyProvider { get; }

    public IdentityConfigurationFingerprint Fingerprint { get; }

    public DocumentFormatRegistry Formats { get; }

    public DocumentGeometryInspector Geometry { get; }

    public DocumentPictureAnalyzer Pictures { get; }

    public DocumentSideClassifier Sides { get; }

    public IZonedFieldReader ZonedFields { get; }

    public IMrzExtractionService Mrz { get; }

    public CaptureQualityAssessor CaptureQuality { get; }

    public DocumentCoherenceChecker Coherence { get; }

    public FrameSequenceReader FrameReader { get; }

    public PresenceSequenceValidator Validator { get; }

    public PresenceFrameSelector Selector { get; }

    public SequenceLivenessEvaluator Liveness { get; }

    public PresenceMatcher Matcher { get; }

    public IdentityDecisionEngine Engine { get; }

    public IBiometricCandidateComparison CandidateComparison { get; }

    /// <summary>The policy in force, read once; ready (the run does not start otherwise).</summary>
    public IdentityPolicySnapshot PolicySnapshot => PolicyProvider.Snapshot;

    public IdentityDecisionPolicy ActivePolicy => PolicySnapshot is { IsReady: true } snapshot
        ? snapshot.Policy
        : throw new QualificationRunException(QualificationRunCodes.PolicyNotReady, PolicySnapshot.ReasonCode);

    public IReadOnlyList<string> ProtectedPolicyFolders()
    {
        return QualificationOutputFolder.ProtectedPolicyFolders(IdentityOptions.PolicyFile, AppContext.BaseDirectory);
    }

    public void EnsureOutputAllowed(string path)
    {
        QualificationOutputFolder.EnsureAllowed(path, ProtectedPolicyFolders(), QualificationOutputFolder.CheckoutRoot(AppContext.BaseDirectory));
    }

    public bool HasRecognitionCandidate => Models.FaceRecognitionCandidate is not null;

    public bool HasAntiSpoofingCandidate => Models.AntiSpoofingCandidate is not null;

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
        return Describe(options).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    internal static ServiceCollection Describe(QualificationPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        if (options.IncludeEnvironmentVariables)
            configuration.AddEnvironmentVariables();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration.Build());
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton(options.Clock);
        services.AddSingleton<IFeatureFlags>(new QualificationFlags());

        services.AddOptions<BiometricModelOptions>().BindConfiguration(BiometricModelOptions.SectionName);
        services.AddSingleton<BiometricModelStore>();
        services.AddSingleton<IBiometricModelStore>(provider => provider.GetRequiredService<BiometricModelStore>());
        services.AddSingleton<IFaceDetector, FaceDetector>();
        services.AddSingleton<IFaceEmbedder, FaceEmbedder>();
        services.AddScoped<IAntiSpoofingService, AntiSpoofingService>();
        services.AddSingleton<CaptureQualityAssessor>();

        services.AddOptions<MrzOcrOptions>().BindConfiguration(MrzOcrOptions.SectionName);
        services.AddSingleton<IMrzBandLocator, MrzImageLocator>();
        services.AddSingleton<TesseractMrzTextReader>();
        services.AddSingleton<IMrzTextReader>(provider => provider.GetRequiredService<TesseractMrzTextReader>());
        services.AddSingleton<TesseractPageTextReader>();
        services.AddSingleton<IPageTextReader>(provider => provider.GetRequiredService<TesseractPageTextReader>());
        services.AddScoped<IMrzExtractionService, MrzExtractionService>();

        services.AddSelfHostedIdentityVerification();

        services.PostConfigure<BiometricModelOptions>(settings =>
        {
            settings.VerificationTimeoutMs = Math.Max(settings.VerificationTimeoutMs, WidenedModelTimeoutMs);
            settings.LivenessTimeoutMs = Math.Max(settings.LivenessTimeoutMs, WidenedModelTimeoutMs);
        });
        services.PostConfigure<IdentityVerificationOptions>(settings => settings.Presence.SequenceTimeoutMs = Math.Max(settings.Presence.SequenceTimeoutMs, WidenedSequenceTimeoutMs));
        services.PostConfigure<MrzOcrOptions>(settings => settings.TimeoutSeconds = Math.Max(settings.TimeoutSeconds, WidenedOcrTimeoutSeconds));

        options.ConfigureServices?.Invoke(services);
        return services;
    }

    private sealed class QualificationFlags : IFeatureFlags
    {
        public bool IsEnabled(string flag)
        {
            return flag is FeatureFlags.CheckInIdentityVerification or FeatureFlags.CheckInIdentitySelfHosted;
        }
    }
}

/// <summary>
/// Where a qualification may write (E74/F01/US02, NF-198, ADR-013-R2): the results of a run hold the scores of a dataset that is kept outside the repository (it is public), the lock and the
/// report are what a person reviews, and a proposed policy is a NEW file that a person commits on purpose. So an output folder is never a folder of the committed policy files (the one the
/// application reads them from, next to the test assembly, and the one of the sources) and never a folder of the checkout the tests were built from. A folder INSIDE one of them is refused too.
/// The rules are pure (paths in, a refusal or nothing out), so that they are proved without a pipeline.
/// </summary>
public static class QualificationOutputFolder
{
    public const string PolicyFolderDetail = "policy_folder";

    public const string CheckoutDetail = "checkout";

    public static string? CheckoutRoot(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
                return directory.FullName;
        }

        return null;
    }

    /// <summary>
    /// The folders that hold the committed policy files: the folder of <paramref name="policyFile"/> (the setting <c>IdentityVerification:PolicyFile</c>, relative to
    /// <paramref name="baseDirectory"/> unless it is rooted) and, when there is a checkout above <paramref name="baseDirectory"/>, the folder of the sources (<c>Casazen.Web/IdentityPolicies</c>).
    /// </summary>
    public static IReadOnlyList<string> ProtectedPolicyFolders(string policyFile, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var path = Path.IsPathRooted(policyFile) ? policyFile : Path.GetFullPath(Path.Combine(baseDirectory, policyFile));
        var folders = new List<string>();
        if (Path.GetDirectoryName(path) is { Length: > 0 } published)
            folders.Add(published);

        if (CheckoutRoot(baseDirectory) is { } root)
        {
            var sources = Path.Combine(root, "Casazen.Web", "IdentityPolicies");
            if (Directory.Exists(sources))
                folders.Add(sources);
        }

        return folders;
    }

    public static void EnsureAllowed(string path, IReadOnlyList<string> protectedFolders, string? checkoutRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protectedFolders);

        if (protectedFolders.Any(folder => IsInside(path, folder)))
            throw new QualificationRunException(QualificationRunCodes.OutputFolderInvalid, PolicyFolderDetail);

        if (checkoutRoot is not null && IsInside(path, checkoutRoot))
            throw new QualificationRunException(QualificationRunCodes.OutputFolderInvalid, CheckoutDetail);
    }

    public static bool IsInside(string path, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(candidate, root, comparison) || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
