using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class ActivePoseVerificationServiceTests
{
    private static DetectedFace Face(float offset) =>
        new(8, 8, 48, 48, 0.99f, [16, 22, 48, 22, 32 + offset, 33, 22, 45, 42, 45], .56);

    private static ActivePoseVerificationService Service(ScriptedFaceDetector detector, ScriptedFaceEmbedder embedder) =>
        new(detector, embedder, Options.Create(new BiometricModelOptions()), NullLogger<ActivePoseVerificationService>.Instance);

    private static MemoryStream Frame(int seed) => new(TestImages.Png(64, 64, seed));

    [Theory]
    [InlineData(0, 10, true, true)]
    [InlineData(0, -10, false, true)]
    [InlineData(0, 10, false, false)]
    [InlineData(0, -10, true, false)]
    [InlineData(0, 3, true, false)]
    [InlineData(10, 18, true, false)]
    [InlineData(4, 8, true, false)]
    public async Task Verify_RequiresFrontalStartAndDistinctTurnInRequestedDirection(float firstOffset, float secondOffset,
        bool right, bool expected)
    {
        var detector = new ScriptedFaceDetector([Face(firstOffset)], [Face(secondOffset)]);
        var embedder = new ScriptedFaceEmbedder([1, 0], [1, 0]);
        var result = await Service(detector, embedder).VerifyAsync(Frame(1), Frame(2), right, .85, default);

        Assert.Equal(expected, result);
        Assert.Equal(expected ? 2 : 0, embedder.Calls);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, pixel => Assert.Equal((byte)0, pixel)));
        Assert.All(embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));
    }

    [Fact]
    public async Task Verify_RejectsTheSamePixelsBeforeInference()
    {
        var detector = new ScriptedFaceDetector();
        Assert.False(await Service(detector, new ScriptedFaceEmbedder()).VerifyAsync(Frame(1), Frame(1), true, .85, default));
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public async Task Verify_RejectsADifferentSubjectDespiteTheRightPose()
    {
        var detector = new ScriptedFaceDetector([Face(0)], [Face(10)]);
        var embedder = new ScriptedFaceEmbedder([1, 0], [0, 1]);
        Assert.False(await Service(detector, embedder).VerifyAsync(Frame(1), Frame(2), true, .85, default));
        Assert.Equal(2, embedder.Calls);
    }

    [Fact]
    public async Task Verify_RejectsMultipleFacesUnreadableInputAndModelErrors()
    {
        var multiple = new ScriptedFaceDetector([Face(0)], [Face(10), Face(10)]);
        var embedder = new ScriptedFaceEmbedder();
        Assert.False(await Service(multiple, embedder).VerifyAsync(Frame(1), Frame(2), true, .85, default));
        Assert.False(await Service(multiple, embedder).VerifyAsync(new MemoryStream([1, 2]), Frame(2), true, .85, default));
        var unavailable = new ScriptedFaceDetector { Failure = new BiometricUnavailableException() };
        Assert.False(await Service(unavailable, embedder).VerifyAsync(Frame(1), Frame(2), true, .85, default));
        Assert.Equal(0, embedder.Calls);
    }
}
