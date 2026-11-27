using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace Casazen.Tests.Unit.Biometrics;

public class BundledCheckInModelTests
{
    private sealed class Enabled : IFeatureFlags
    {
        public bool IsEnabled(string flag) => flag == FeatureFlags.CheckInIdentityVerification;
    }

    [Fact]
    public async Task PublishedConfiguration_LoadsAndRunsTheThreeBundledModelsOffline()
    {
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").Build();
        var options = config.GetSection("Biometrics").Get<BiometricModelOptions>()!;
        using var store = new BiometricModelStore(Options.Create(options), new Enabled(), NullLogger<BiometricModelStore>.Instance);
        await store.LoadAsync(CancellationToken.None);
        Assert.True(store.IsAvailable, string.Join(", ", store.Snapshot.Models));
        foreach (var model in new[] { store.FaceDetector!, store.AntiSpoofing!, store.FaceRecognition! })
        {
            var shape = new long[] { 1, 3, model.InputHeight, model.InputWidth };
            using var input = OrtValue.CreateTensorValueFromMemory(new float[3 * model.InputHeight * model.InputWidth], shape);
            using var run = new RunOptions();
            using var result = model.Session.Run(run, new Dictionary<string, OrtValue> { [model.InputName] = input }, model.Session.OutputNames);
            Assert.All(result, output => Assert.True(output.GetTensorDataAsSpan<float>().ToArray().All(float.IsFinite)));
        }
    }
}
