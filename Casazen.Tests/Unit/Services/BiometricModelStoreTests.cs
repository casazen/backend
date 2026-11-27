using System.Net;
using System.Security.Cryptography;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T01 (ADR-013-R8, R14): a model file is used only when its SHA-256 equals the pinned one; whatever else it is (missing, not
/// pinned, different, not a model, not downloadable) the models are not available and the check-in goes to the operator.
/// </summary>
public sealed class BiometricModelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cz-biometrics-" + Guid.NewGuid().ToString("N"));

    public BiometricModelStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed class Flag(bool on) : IFeatureFlags
    {
        public bool IsEnabled(string flag) => on && flag == FeatureFlags.CheckInIdentityVerification;
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static ModelFileOptions Spec(string path, string sha256, string downloadUrl = "") =>
        new() { Name = "TestModel", Version = "1", License = "MIT", Path = path, Sha256 = sha256, DownloadUrl = downloadUrl };

    private static Task<ModelFileCheck> Ensure(ModelFileOptions spec, string folder, Func<HttpClient>? http = null) =>
        ModelFileProvisioner.EnsureAsync(spec, folder, http, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Ensure_HashNotPinned_RefusesAFileThatIsThere()
    {
        var path = Write("model.onnx", [1, 2, 3]);

        var check = await Ensure(Spec(path, string.Empty), _folder);

        Assert.False(check.IsVerified);
        Assert.Equal(ModelFileProvisioner.HashNotConfigured, check.Failure);
        Assert.Null(check.Path);
    }

    [Fact]
    public async Task Ensure_MissingFile_WithoutAnAddress_IsFileMissing()
    {
        var check = await Ensure(Spec("model.onnx", Sha256Of([1])), _folder);

        Assert.Equal(ModelFileProvisioner.FileMissing, check.Failure);
    }

    [Fact]
    public async Task Ensure_OtherBytes_IsHashMismatch()
    {
        Write("model.onnx", [1, 2, 3]);

        var check = await Ensure(Spec("model.onnx", Sha256Of([9, 9, 9])), _folder);

        Assert.Equal(ModelFileProvisioner.HashMismatch, check.Failure);
        Assert.Null(check.Path);
    }

    [Fact]
    public async Task Ensure_PinnedBytes_ReturnsTheFullPathAndTheHash()
    {
        byte[] bytes = [1, 2, 3, 4];
        Write("model.onnx", bytes);

        var check = await Ensure(Spec("model.onnx", Sha256Of(bytes).ToLowerInvariant()), _folder);

        Assert.True(check.IsVerified);
        Assert.Equal(Path.Combine(_folder, "model.onnx"), check.Path);
        Assert.Equal(Sha256Of(bytes), check.Sha256);
    }

    [Fact]
    public async Task Ensure_Download_KeepsTheFileOnlyWhenTheHashMatches()
    {
        byte[] bytes = [7, 7, 7, 7, 7];
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var check = await Ensure(
            Spec("sub/model.onnx", Sha256Of(bytes), "https://models.example.test/model.onnx"),
            _folder,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.True(check.IsVerified);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_folder, "sub", "model.onnx")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "sub"), "*.partial"));
    }

    [Fact]
    public async Task Ensure_Download_OtherBytes_AreDeleted()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 1, 1]) });

        var check = await Ensure(
            Spec("model.onnx", Sha256Of([2, 2, 2]), "https://models.example.test/model.onnx"),
            _folder,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.Equal(ModelFileProvisioner.HashMismatch, check.Failure);
        Assert.False(File.Exists(Path.Combine(_folder, "model.onnx")));
        Assert.Empty(Directory.GetFiles(_folder, "*.partial"));
    }

    [Fact]
    public async Task Ensure_Download_ServerError_IsDownloadFailed()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var check = await Ensure(
            Spec("model.onnx", Sha256Of([2]), "https://models.example.test/model.onnx"),
            _folder,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.Equal(ModelFileProvisioner.DownloadFailed, check.Failure);
        Assert.False(File.Exists(Path.Combine(_folder, "model.onnx")));
    }

    [Fact]
    public async Task Ensure_Download_WithoutAPinnedHash_NeverGoesToTheNetwork()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        var check = await Ensure(
            Spec("model.onnx", string.Empty, "https://models.example.test/model.onnx"),
            _folder,
            () => new HttpClient(handler, disposeHandler: false));

        Assert.Equal(ModelFileProvisioner.HashNotConfigured, check.Failure);
        Assert.Equal(0, handler.Calls);
    }

    private BiometricModelStore Store(BiometricModelOptions options, bool featureOn = true) =>
        new(Options.Create(options), new Flag(featureOn), NullLogger<BiometricModelStore>.Instance) { BaseDirectory = _folder };

    [Fact]
    public async Task Store_FeatureOff_LoadsNothing()
    {
        using var store = Store(new BiometricModelOptions(), featureOn: false);

        await store.StartAsync(CancellationToken.None);
        await store.ExecuteTask!;

        Assert.Equal(BiometricModelsState.Disabled, store.Snapshot.State);
        Assert.False(store.IsAvailable);
        Assert.Null(store.FaceDetector);
        Assert.Null(store.AntiSpoofing);
        Assert.Null(store.FaceRecognition);
    }

    [Fact]
    public async Task Store_NothingPinned_IsUnavailableAndSaysWhyForEveryModel()
    {
        using var store = Store(new BiometricModelOptions());

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.IsAvailable);
        Assert.Equal(BiometricModelsState.Unavailable, store.Snapshot.State);
        Assert.Equal(ModelFileProvisioner.HashNotConfigured, store.Snapshot.ReasonCode);
        Assert.Equal(3, store.Snapshot.Models.Count);
        Assert.All(store.Snapshot.Models, model =>
        {
            Assert.False(model.Loaded);
            Assert.Equal(ModelFileProvisioner.HashNotConfigured, model.FailureCode);
        });
        Assert.Null(store.FaceDetector);
    }

    [Fact]
    public async Task Store_ModelWithOtherBytes_IsNotUsed()
    {
        Write("detector.onnx", [1, 2, 3]);
        var options = new BiometricModelOptions();
        options.FaceDetector.Path = "detector.onnx";
        options.FaceDetector.Sha256 = Sha256Of([4, 5, 6]);
        using var store = Store(options);

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.IsAvailable);
        Assert.Equal(ModelFileProvisioner.HashMismatch, store.Snapshot.ReasonCode);
        Assert.Equal(ModelFileProvisioner.HashMismatch, store.Snapshot.Models.First(m => m.Role == "face_detector").FailureCode);
        Assert.Null(store.FaceDetector);
    }

    [Fact]
    public async Task Store_PinnedBytesThatAreNotAModel_AreNotUsed()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];
        Write("detector.onnx", bytes);
        var options = new BiometricModelOptions();
        options.FaceDetector.Path = "detector.onnx";
        options.FaceDetector.Sha256 = Sha256Of(bytes);
        using var store = Store(options);

        await store.LoadAsync(CancellationToken.None);

        var detector = store.Snapshot.Models.First(m => m.Role == "face_detector");
        Assert.False(detector.Loaded);
        Assert.Equal("load_failed", detector.FailureCode);
        Assert.False(store.IsAvailable);
    }

    [Fact]
    public async Task Store_ConfigurationThatCannotBeUsed_IsConfigInvalid()
    {
        var options = new BiometricModelOptions();
        options.FaceRecognition.Sha256 = "not-a-hash";
        using var store = Store(options);

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(BiometricModelsState.Unavailable, store.Snapshot.State);
        Assert.Equal("config_invalid", store.Snapshot.ReasonCode);
        Assert.False(store.IsAvailable);
    }

    [Fact]
    public void Options_Defaults_AreUsableAndPinNothing()
    {
        var options = new BiometricModelOptions();

        Assert.Empty(options.Validate());
        Assert.False(options.FaceDetector.HasWellFormedSha256());
        Assert.False(options.AntiSpoofing.HasWellFormedSha256());
        Assert.False(options.FaceRecognition.HasWellFormedSha256());
        Assert.Equal(BiometricModelOptions.DefaultVerificationTimeoutMs, options.VerificationTimeoutMs);
        Assert.True(options.VerificationTimeoutMs < 3000, "ADR-013-R1: an answer within three seconds");
    }

    [Theory]
    [InlineData("../outside.onnx")]
    [InlineData("a/../../outside.onnx")]
    public void Options_PathThatLeavesTheApplicationFolder_IsRefused(string path)
    {
        var options = new BiometricModelOptions();
        options.FaceDetector.Path = path;

        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void Options_DownloadUrlThatIsNotHttps_IsRefused()
    {
        var options = new BiometricModelOptions();
        options.FaceRecognition.DownloadUrl = "http://models.example.test/model.onnx";

        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void Options_ThresholdsOutOfRange_AreRefused()
    {
        var options = new BiometricModelOptions { LivenessThreshold = 1.5, VerificationTimeoutMs = 10 };

        Assert.True(options.Validate().Count >= 2);
    }

    // ─── The optional second candidates (E72/F01/US02/T03, decision D6) ──────────────────────────────────

    [Fact]
    public async Task Store_NoCandidateConfigured_IsReady_AndEachCandidateIsNotConfigured()
    {
        using var store = await BundledBiometricModels.LoadAsync();

        // The store is as it was: ready, no reason, the three models of the decisions loaded.
        Assert.True(store.IsAvailable);
        Assert.Equal(BiometricModelsState.Ready, store.Snapshot.State);
        Assert.Null(store.Snapshot.ReasonCode);
        Assert.Equal(3, store.Snapshot.Models.Count);
        Assert.All(store.Snapshot.Models, model => Assert.True(model.Loaded));

        // The candidates are listed apart and say that they are not configured; nothing is loaded for them.
        Assert.Equal([BiometricModelRoles.AntiSpoofingCandidate, BiometricModelRoles.FaceRecognitionCandidate], store.Snapshot.Candidates.Select(candidate => candidate.Role));
        Assert.All(store.Snapshot.Candidates, candidate =>
        {
            Assert.False(candidate.Loaded);
            Assert.Null(candidate.WeightHash);
            Assert.Equal(BiometricModelStatusStates.NotConfigured, candidate.FailureCode);
        });
        Assert.Null(store.AntiSpoofingCandidate);
        Assert.Null(store.FaceRecognitionCandidate);
    }

    [Fact]
    public async Task Store_ACandidateThatIsAFileWithItsRealHash_IsLoadedLikeTheThreeModels_AndNothingElseChanges()
    {
        var options = BundledBiometricModels.PublishedOptions();
        options.FaceRecognitionCandidate = CandidateOf<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition.onnx");
        options.AntiSpoofingCandidate = CandidateOf<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "antispoofing.onnx");
        using var store = await BundledBiometricModels.LoadAsync(options);

        Assert.NotNull(store.FaceRecognitionCandidate);
        Assert.NotNull(store.AntiSpoofingCandidate);
        Assert.Equal(options.FaceRecognition.Sha256, store.FaceRecognitionCandidate.WeightHash, ignoreCase: true);
        Assert.All(store.Snapshot.Candidates, candidate =>
        {
            Assert.True(candidate.Loaded);
            Assert.Null(candidate.FailureCode);
            Assert.Equal("test candidate", candidate.Provenance);
        });

        // The three models of the decisions are the ones they were, and the weights hash a decision is bound to ignores the candidates.
        Assert.True(store.IsAvailable);
        Assert.Equal(BiometricModelsState.Ready, store.Snapshot.State);
        Assert.Null(store.Snapshot.ReasonCode);
        Assert.Equal(
            [BiometricModelRoles.FaceDetector, BiometricModelRoles.AntiSpoofing, BiometricModelRoles.FaceRecognition],
            store.Snapshot.Models.Select(model => model.Role));
        Assert.All(store.Snapshot.Models, model => Assert.True(model.Loaded));
        var withoutCandidates = store.Snapshot with { Candidates = [] };
        Assert.NotNull(IdentityConfigurationFingerprint.ComputeWeightsHash(store.Snapshot));
        Assert.Equal(IdentityConfigurationFingerprint.ComputeWeightsHash(withoutCandidates), IdentityConfigurationFingerprint.ComputeWeightsHash(store.Snapshot));
    }

    [Theory]
    [InlineData("another hash", "hash_mismatch")]
    [InlineData("no hash", "hash_not_configured")]
    [InlineData("no file", "file_missing")]
    [InlineData("a file that is not a model", "load_failed")]
    [InlineData("no name", "config_invalid")]
    [InlineData("no path", "config_invalid")]
    [InlineData("a hash that is not a hash", "config_invalid")]
    [InlineData("a path that leaves the folder", "config_invalid")]
    [InlineData("an address that is not https", "config_invalid")]
    [InlineData("an input size out of range", "config_invalid")]
    [InlineData("a divisor of 0", "config_invalid")]
    public async Task Store_ACandidateThatCannotBeUsed_IsNotLoadedWithItsCode_AndTheThreeModelsAreUntouched(string situation, string expectedCode)
    {
        var options = BundledBiometricModels.PublishedOptions();
        var candidate = CandidateOf<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition.onnx");
        switch (situation)
        {
            case "another hash":
                candidate.Sha256 = new string('B', 64);
                break;
            case "no hash":
                candidate.Sha256 = string.Empty;
                break;
            case "no file":
                candidate.Path = Path.Combine(_folder, "absent.onnx");
                break;
            case "a file that is not a model":
                var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                File.WriteAllBytes(Path.Combine(_folder, "junk.onnx"), junk);
                candidate.Path = Path.Combine(_folder, "junk.onnx");
                candidate.Sha256 = Sha256Of(junk);
                break;
            case "no name":
                candidate.Name = string.Empty;
                break;
            case "no path":
                candidate.Path = string.Empty;
                break;
            case "a hash that is not a hash":
                candidate.Sha256 = "not-a-hash";
                break;
            case "a path that leaves the folder":
                candidate.Path = "../outside.onnx";
                break;
            case "an address that is not https":
                candidate.DownloadUrl = "http://models.example.test/model.onnx";
                break;
            case "an input size out of range":
                candidate.InputSize = 4;
                break;
            default:
                candidate.Std = 0;
                break;
        }

        options.FaceRecognitionCandidate = candidate;

        // A candidate that is wrong is its own problem: the settings of the three models are still valid, so the store is not "config_invalid".
        Assert.Empty(options.Validate());
        using var store = await BundledBiometricModels.LoadAsync(options);

        var info = Assert.Single(store.Snapshot.Candidates, found => found.Role == BiometricModelRoles.FaceRecognitionCandidate);
        Assert.False(info.Loaded);
        Assert.Equal(expectedCode, info.FailureCode);
        Assert.Null(store.FaceRecognitionCandidate);
        Assert.Equal(
            BiometricModelStatusStates.NotConfigured,
            Assert.Single(store.Snapshot.Candidates, found => found.Role == BiometricModelRoles.AntiSpoofingCandidate).FailureCode);

        Assert.True(store.IsAvailable);
        Assert.Equal(BiometricModelsState.Ready, store.Snapshot.State);
        Assert.Null(store.Snapshot.ReasonCode);
        Assert.Equal(3, store.Snapshot.Models.Count);
        Assert.All(store.Snapshot.Models, model => Assert.True(model.Loaded));
        Assert.NotNull(store.FaceDetector);
        Assert.NotNull(store.AntiSpoofing);
        Assert.NotNull(store.FaceRecognition);
    }

    [Theory]
    [InlineData("a crop scale out of range")]
    [InlineData("an input size out of range")]
    [InlineData("no name")]
    public async Task Store_AnAntiSpoofingCandidateWithAnInvalidDescription_IsConfigInvalid_AndTheStoreIsReady(string situation)
    {
        var options = BundledBiometricModels.PublishedOptions();
        var candidate = CandidateOf<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "antispoofing.onnx");
        switch (situation)
        {
            case "a crop scale out of range":
                candidate.CropScale = 9;
                break;
            case "an input size out of range":
                candidate.InputSize = 1000;
                break;
            default:
                candidate.Name = " ";
                break;
        }

        options.AntiSpoofingCandidate = candidate;
        using var store = await BundledBiometricModels.LoadAsync(options);

        Assert.Equal("config_invalid", Assert.Single(store.Snapshot.Candidates, found => found.Role == BiometricModelRoles.AntiSpoofingCandidate).FailureCode);
        Assert.Null(store.AntiSpoofingCandidate);
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public async Task Store_WhenTheThreeModelsCannotBeUsed_TheCandidatesAreNotLoaded_AndSayNothingOfTheState()
    {
        // The candidate is a file with its hash, but the three models are not there: the store is unavailable for its own reason and does not look at the candidates.
        var options = new BiometricModelOptions { FaceRecognitionCandidate = CandidateOf<FaceRecognitionCandidateOptions>(new ModelFileOptions { Path = "x", Sha256 = new string('C', 64) }, "recognition.onnx") };
        using var store = Store(options);

        await store.LoadAsync(CancellationToken.None);

        Assert.False(store.IsAvailable);
        Assert.Equal(BiometricModelsState.Unavailable, store.Snapshot.State);
        Assert.Equal(ModelFileProvisioner.HashNotConfigured, store.Snapshot.ReasonCode);
        Assert.Empty(store.Snapshot.Candidates);
        Assert.Null(store.FaceRecognitionCandidate);
    }

    [Fact]
    public void Options_TheCandidates_AreNotConfiguredByDefault_AndAreValidated_OnlyWhenConfigured()
    {
        var options = new BiometricModelOptions();
        Assert.False(BiometricModelOptions.IsCandidateConfigured(options.AntiSpoofingCandidate));
        Assert.False(BiometricModelOptions.IsCandidateConfigured(options.FaceRecognitionCandidate));
        Assert.Empty(options.ValidateAntiSpoofingCandidate());
        Assert.Empty(options.ValidateFaceRecognitionCandidate());

        // Anything of the file is "configured": a path, a hash or an address, and then the rest is asked for.
        options.AntiSpoofingCandidate.Sha256 = new string('D', 64);
        options.FaceRecognitionCandidate.DownloadUrl = "https://models.example.test/model.onnx";
        Assert.True(BiometricModelOptions.IsCandidateConfigured(options.AntiSpoofingCandidate));
        Assert.True(BiometricModelOptions.IsCandidateConfigured(options.FaceRecognitionCandidate));
        Assert.NotEmpty(options.ValidateAntiSpoofingCandidate());
        Assert.NotEmpty(options.ValidateFaceRecognitionCandidate());

        // Their reference defaults: the way MiniFASNetV2 and SFace are run, which the candidates are described against.
        Assert.Equal((2.7, 80), (new AntiSpoofingCandidateOptions().CropScale, new AntiSpoofingCandidateOptions().InputSize));
        var recognition = new FaceRecognitionCandidateOptions();
        Assert.Equal((112, 0.0, 1.0, true), (recognition.InputSize, recognition.Mean, recognition.Std, recognition.RgbOrder));

        // And the three models are valid whatever the candidates are: they have their own validation.
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Options_ACandidateRunsWithItsOwnDescription_TheReferenceSettingsAreNotChanged()
    {
        var options = new BiometricModelOptions();
        options.AntiSpoofingCandidate.CropScale = 4;
        options.AntiSpoofingCandidate.InputSize = 128;
        options.FaceRecognitionCandidate.InputSize = 128;
        options.FaceRecognitionCandidate.Mean = 127.5;
        options.FaceRecognitionCandidate.Std = 127.5;
        options.FaceRecognitionCandidate.RgbOrder = false;

        var presence = options.ForAntiSpoofingCandidate();
        var recognition = options.ForFaceRecognitionCandidate();

        Assert.Equal((4.0, 128), (presence.LivenessCropScale, presence.LivenessInputSize));
        Assert.Equal((128, 127.5, 127.5, false), (recognition.RecognitionInputSize, recognition.RecognitionMean, recognition.RecognitionStd, recognition.RecognitionRgbOrder));
        Assert.Equal((2.7, 80, 112, 0.0, 1.0, true), (options.LivenessCropScale, options.LivenessInputSize, options.RecognitionInputSize, options.RecognitionMean, options.RecognitionStd, options.RecognitionRgbOrder));
    }

    /// <summary>A copy of a bundled model in the temporary folder, described as a candidate (the hash of the copy is the one of the original).</summary>
    private T CandidateOf<T>(ModelFileOptions bundled, string fileName)
        where T : ModelFileOptions, new()
    {
        var target = Path.Combine(_folder, fileName);
        var source = Path.Combine(AppContext.BaseDirectory, bundled.Path);
        if (File.Exists(source) && !File.Exists(target))
            File.Copy(source, target);

        return new T
        {
            Name = "Candidate of the tests",
            Version = "1",
            License = "Apache-2.0",
            Provenance = "test candidate",
            Path = target,
            Sha256 = bundled.Sha256,
        };
    }
}
