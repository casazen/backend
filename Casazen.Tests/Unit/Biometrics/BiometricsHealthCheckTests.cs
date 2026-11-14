using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.Extensions;
using Casazen.Web.HealthChecks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Biometrics;

/// <summary>
/// E22/F01/US01/T01: the <c>biometrics</c> health entry and <c>GET api/admin/biometrics/status</c> say which model is missing and why, without ever taking
/// the instance out of rotation and without a path, a hash or an address in the answer. Both read the same <see cref="IBiometricModelStatusReader"/>.
/// </summary>
public sealed class BiometricsHealthCheckTests : IDisposable
{
    private const string SecretPath = "MlModels/biometrics/never-shown-path.onnx";
    private const string PinnedHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string DownloadAddress = "https://models.example.test/never-shown/glintr100.onnx";

    private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cz-biometrics-health-" + Guid.NewGuid().ToString("N"));

    public BiometricsHealthCheckTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): the operator path is never blocked by the models. Whatever the state of the store (not loaded, loading, ready, unavailable as a whole
    /// or model by model), with the flag on or off, with the files pinned or not, and even when the reading itself fails, the entry is <c>Healthy</c> or
    /// <c>Degraded</c>, never <c>Unhealthy</c> (which would be a 503 on <c>/api/health/ready</c> and an instance out of rotation); and the registration
    /// itself maps a failure of the check to <c>Degraded</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Situations))]
    public async Task BiometricsHealth_NeverUnhealthy_Test(string situation, bool featureOn, BiometricModelsSnapshot snapshot, bool pinned)
    {
        var check = new BiometricsHealthCheck(Reader(featureOn, snapshot, pinned));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.True(result.Status is HealthStatus.Healthy or HealthStatus.Degraded, $"{situation}: the entry was {result.Status}");
        Assert.NotNull(result.Description);
    }

    [Fact]
    public async Task BiometricsHealth_TheReadingFails_IsDegradedNotUnhealthy_Test()
    {
        var check = new BiometricsHealthCheck(new ThrowingReader());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task BiometricsHealth_TheRegistration_MapsAFailureOfTheCheckToDegraded_Test()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddCasazenHealthChecks();
        await using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Single(r => r.Name == "biometrics");

        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains(HealthCheckExtensions.ReadyTag, registration.Tags);
    }

    /// <summary>
    /// Proof (permission): the status is for the platform administrators only. The route sits under <see cref="CasazenPolicies.AdminOnly"/> and nothing else
    /// opens it (no <c>[AllowAnonymous]</c>, no host policy); the real registered policy refuses the owner of an organization and every caller that is not an
    /// administrator, while the administrator passes (the positive control).
    /// </summary>
    [Fact]
    public async Task AdminBiometricsStatus_AdminOnly_Test()
    {
        var controller = typeof(AdminBiometricsController);
        Assert.Equal([CasazenPolicies.AdminOnly], controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal("api/admin/biometrics", controller.GetCustomAttribute<RouteAttribute>()!.Template);

        var routed = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToList();
        var action = Assert.Single(routed);
        Assert.Equal("GetStatus", action.Name);
        Assert.Equal(["GET"], action.GetCustomAttribute<HttpMethodAttribute>()!.HttpMethods);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.DoesNotContain(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != CasazenPolicies.AdminOnly);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(["Admin"], requirement.AllowedRoles);

        foreach (var role in new[] { "PropertyOwner", "PropertyManager", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, Principal(role), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the models status.");
        }

        var nobody = new AuthorizationHandlerContext(policy.Requirements, Principal(), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(nobody);
        Assert.False(nobody.HasSucceeded);

        var admin = new AuthorizationHandlerContext(policy.Requirements, Principal("Admin"), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(admin);
        Assert.True(admin.HasSucceeded, "The platform administrator does not pass the policy of the models status.");
    }

    // ─── What the answers say ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BiometricsHealth_FlagOff_IsHealthyWhateverTheFilesAre()
    {
        var check = new BiometricsHealthCheck(Reader(featureOn: false, new BiometricModelsSnapshot(BiometricModelsState.Disabled, null, []), pinned: false));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("off", result.Description);
    }

    [Fact]
    public async Task BiometricsHealth_EveryModelReady_IsHealthy()
    {
        var reader = Reader(featureOn: true, Ready(), pinned: true, ocrFile: true);

        var result = await new BiometricsHealthCheck(reader).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(BiometricModelStatusStates.Ready, reader.Read().State);
    }

    [Fact]
    public async Task BiometricsHealth_ModelsThatCannotBeUsed_AreNamedWithTheirCode_AndNeverWithAPathAHashOrAnAddress()
    {
        var snapshot = new BiometricModelsSnapshot(
            BiometricModelsState.Unavailable,
            "hash_not_configured",
            [
                Info(BiometricModelRoles.FaceDetector, loaded: true, null),
                Info(BiometricModelRoles.AntiSpoofing, loaded: false, "file_missing"),
                Info(BiometricModelRoles.FaceRecognition, loaded: false, "hash_not_configured"),
            ]);

        var result = await new BiometricsHealthCheck(Reader(featureOn: true, snapshot, pinned: true)).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("anti_spoofing: file_missing", result.Description);
        Assert.Contains("face_recognition: hash_not_configured", result.Description);
        Assert.DoesNotContain("face_detector", result.Description);
        Assert.DoesNotContain(SecretPath, result.Description);
        Assert.DoesNotContain(PinnedHash, result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DownloadAddress, result.Description);
    }

    [Fact]
    public void Describe_TheOcrFile_IsFailedUntilItIsPinnedAndOnDisk_ThenPresent()
    {
        var snapshot = Ready();
        var options = new BiometricModelOptions();
        File.WriteAllBytes(System.IO.Path.Combine(_folder, "ocrb.traineddata"), [1, 2, 3]);

        MrzOcrOptions Mrz(string sha256, string path) => new() { Mrz = new ModelFileOptions { Name = "OCR-B", Path = path, Sha256 = sha256 } };

        var notPinned = BiometricModelStatuses.Describe(true, snapshot, options, Mrz(string.Empty, "ocrb.traineddata"), _folder);
        var missing = BiometricModelStatuses.Describe(true, snapshot, options, Mrz(PinnedHash, "absent.traineddata"), _folder);
        var present = BiometricModelStatuses.Describe(true, snapshot, options, Mrz(PinnedHash, "ocrb.traineddata"), _folder);

        Assert.Equal("hash_not_configured", OcrOf(notPinned).Code);
        Assert.False(OcrOf(notPinned).Pinned);
        Assert.Equal("file_missing", OcrOf(missing).Code);
        Assert.Equal(BiometricModelStatusStates.Present, OcrOf(present).State);
        Assert.True(OcrOf(present).Pinned);
        Assert.Equal(BiometricModelStatusStates.Unavailable, notPinned.State);
        Assert.Equal(BiometricModelStatusStates.Ready, present.State);
    }

    [Fact]
    public void Status_TheAnswerIsAClosedShape_WithNoPathNoHashAndNoAddress()
    {
        var snapshot = new BiometricModelsSnapshot(
            BiometricModelsState.Unavailable,
            "file_missing",
            [
                Info(BiometricModelRoles.FaceDetector, loaded: true, null),
                Info(BiometricModelRoles.AntiSpoofing, loaded: true, null),
                Info(BiometricModelRoles.FaceRecognition, loaded: false, "file_missing"),
            ]);

        var dto = BiometricsStatusDto.From(Reader(featureOn: true, snapshot, pinned: true).Read());
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(SecretPath, json);
        Assert.DoesNotContain(PinnedHash, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DownloadAddress, json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["enabled", "models", "reasonCode", "state"], document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
        {
            Assert.Equal(
                ["code", "license", "name", "pinned", "role", "state", "version"],
                model.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }

        var recognition = dto.Models.Single(m => m.Role == BiometricModelRoles.FaceRecognition);
        Assert.Equal(BiometricModelStatusStates.Failed, recognition.State);
        Assert.Equal("file_missing", recognition.Code);
        Assert.Equal("AuraFace-v1", recognition.Name);
        Assert.Equal("Apache-2.0", recognition.License);
        Assert.True(recognition.Pinned);
        Assert.Equal(BiometricModelStatusStates.Unavailable, dto.State);
    }

    [Fact]
    public void Status_WhileTheModelsLoad_IsLoadingAndNamesNoFailure()
    {
        var status = Reader(featureOn: true, new BiometricModelsSnapshot(BiometricModelsState.Loading, null, []), pinned: true, ocrFile: true).Read();

        Assert.Equal(BiometricModelStatusStates.Loading, status.State);
        Assert.All(status.Models.Where(m => m.Role != BiometricModelRoles.MrzOcr), m => Assert.Null(m.Code));
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, bool, BiometricModelsSnapshot, bool> Situations()
    {
        var data = new TheoryData<string, bool, BiometricModelsSnapshot, bool>();
        var snapshots = new (string Name, BiometricModelsSnapshot Snapshot)[]
        {
            ("not loaded", new(BiometricModelsState.NotLoaded, null, [])),
            ("disabled", new(BiometricModelsState.Disabled, null, [])),
            ("loading", new(BiometricModelsState.Loading, null, [])),
            ("ready", Ready()),
            ("config invalid", new(BiometricModelsState.Unavailable, "config_invalid", [])),
            ("load failed", new(BiometricModelsState.Unavailable, "load_failed", [])),
            (
                "model by model",
                new(
                    BiometricModelsState.Unavailable,
                    "hash_mismatch",
                    [
                        Info(BiometricModelRoles.FaceDetector, loaded: false, "hash_mismatch"),
                        Info(BiometricModelRoles.AntiSpoofing, loaded: false, "download_failed"),
                        Info(BiometricModelRoles.FaceRecognition, loaded: false, "model_incompatible"),
                    ])),
        };

        foreach (var (name, snapshot) in snapshots)
        {
            foreach (var featureOn in new[] { true, false })
            {
                foreach (var pinned in new[] { true, false })
                    data.Add($"{name}, flag {(featureOn ? "on" : "off")}, {(pinned ? "pinned" : "not pinned")}", featureOn, snapshot, pinned);
            }
        }

        return data;
    }

    private BiometricModelStatusReader Reader(bool featureOn, BiometricModelsSnapshot snapshot, bool pinned, bool ocrFile = false)
    {
        var ocrPath = System.IO.Path.Combine(_folder, "ocrb.traineddata");
        if (ocrFile)
            File.WriteAllBytes(ocrPath, [1]);

        var biometrics = new BiometricModelOptions
        {
            FaceDetector = Spec("YuNet", pinned),
            AntiSpoofing = Spec("MiniFASNetV2", pinned),
            FaceRecognition = Spec("AuraFace-v1", pinned, "Apache-2.0"),
        };
        var mrz = new MrzOcrOptions { Mrz = new ModelFileOptions { Name = "OCR-B", Path = ocrPath, Sha256 = pinned ? PinnedHash : string.Empty } };
        return new BiometricModelStatusReader(
            new StoreWithSnapshot(snapshot),
            Options.Create(biometrics),
            Options.Create(mrz),
            new Flags(featureOn));
    }

    private static ModelFileOptions Spec(string name, bool pinned, string license = "MIT") => new()
    {
        Name = name,
        Version = "1",
        License = license,
        Path = SecretPath,
        Sha256 = pinned ? PinnedHash : string.Empty,
        DownloadUrl = DownloadAddress,
    };

    private static BiometricModelsSnapshot Ready() => new(
        BiometricModelsState.Ready,
        null,
        [
            Info(BiometricModelRoles.FaceDetector, loaded: true, null),
            Info(BiometricModelRoles.AntiSpoofing, loaded: true, null),
            Info(BiometricModelRoles.FaceRecognition, loaded: true, null),
        ]);

    private static BiometricModelInfo Info(string role, bool loaded, string? failure) =>
        new(role, "model", "1", "MIT", "provenance", loaded ? PinnedHash : null, loaded, failure);

    private static BiometricModelStatus OcrOf(BiometricModelsStatus status) => status.Models.Single(m => m.Role == BiometricModelRoles.MrzOcr);

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), authenticationType: "test"));

    private sealed class StoreWithSnapshot(BiometricModelsSnapshot snapshot) : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot { get; } = snapshot;

        public bool IsAvailable => Snapshot.IsReady;

        public LoadedBiometricModel? FaceDetector => null;

        public LoadedBiometricModel? AntiSpoofing => null;

        public LoadedBiometricModel? FaceRecognition => null;
    }

    private sealed class Flags(bool on) : IFeatureFlags
    {
        public bool IsEnabled(string flag) => on && flag == FeatureFlags.CheckInIdentityVerification;
    }

    private sealed class ThrowingReader : IBiometricModelStatusReader
    {
        public BiometricModelsStatus Read() => throw new InvalidOperationException("the options could not be bound");
    }
}
