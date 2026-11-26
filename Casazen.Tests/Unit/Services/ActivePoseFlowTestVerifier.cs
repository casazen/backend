using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

internal sealed class ActivePoseFlowTestVerifier : IActivePoseVerificationService
{
    public bool Passed { get; set; } = true;
    public bool? Direction { get; private set; }
    public int Calls { get; private set; }

    public Task<bool> VerifyAsync(Stream neutral, Stream turned, bool noseToRight, double acceptThreshold, CancellationToken cancellationToken)
    {
        AssertReadable(neutral);
        AssertReadable(turned);
        Direction = noseToRight;
        Calls++;
        return Task.FromResult(Passed);
    }

    private static void AssertReadable(Stream image)
    {
        if (image.ReadByte() == -1)
            throw new InvalidDataException("The capture did not reach the verifier.");
    }
}
