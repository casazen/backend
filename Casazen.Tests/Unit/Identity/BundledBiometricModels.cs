using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// The models that ship with the application (<c>Casazen.Web/MlModels</c>: YuNet, MiniFASNetV2 and SFace), loaded the way the application loads them, for the tests that need a real
/// ONNX Runtime session behind the doubles of the faces (E72/F01/US02): the options are the committed <c>appsettings.json</c> (paths and SHA-256 included), with the time limits widened so
/// that a test never depends on how loaded the machine is. Nothing is downloaded and no picture of a person is involved.
/// </summary>
internal static class BundledBiometricModels
{
    public static BiometricModelOptions PublishedOptions()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").Build();
        var options = configuration.GetSection(BiometricModelOptions.SectionName).Get<BiometricModelOptions>()!;
        options.LivenessTimeoutMs = 30_000;
        options.VerificationTimeoutMs = 30_000;
        return options;
    }

    public static async Task<BiometricModelStore> LoadAsync(BiometricModelOptions? options = null)
    {
        var store = new BiometricModelStore(Options.Create(options ?? PublishedOptions()), new CheckInIdentityOn(), NullLogger<BiometricModelStore>.Instance);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private sealed class CheckInIdentityOn : IFeatureFlags
    {
        public bool IsEnabled(string flag) => flag == FeatureFlags.CheckInIdentityVerification;
    }
}
