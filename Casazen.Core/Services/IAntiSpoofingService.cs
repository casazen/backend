namespace Casazen.Core.Services;

public interface IAntiSpoofingService
{
    Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);

    Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

public readonly record struct LivenessResult(bool IsLive, double Score);
