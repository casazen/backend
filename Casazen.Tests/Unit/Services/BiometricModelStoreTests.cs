using System.Net;
using System.Security.Cryptography;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
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
}
