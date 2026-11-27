namespace Casazen.Core.Services;

/// <summary>
/// Anti-spoofing detection for biometric verification (ADR-013, NF-11, NF-24).
/// Uses MiniFASNet (Apache-2.0) to detect photo-of-photo attacks. No code path says "live" without the model having run: an unavailable
/// model, a picture with no face, a timeout and an exception are all "not live".
/// </summary>
public interface IAntiSpoofingService
{
    /// <summary>
    /// Detects if the selfie image is a live capture or a photo-of-photo attack.
    /// </summary>
    /// <param name="imageStream">The selfie image stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the image appears to be a live capture, false if likely spoofed (or not decided).</returns>
    Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scores the selfie with the anti-spoofing model: the score of the real-face class (0 to 1) and whether it reaches the configured threshold
    /// (<c>Biometrics:LivenessThreshold</c>). The image is processed in memory only and zeroed before returning (ADR-013-R2).
    /// </summary>
    /// <param name="imageStream">The selfie image stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result; <c>default</c> (not live, score 0) when the model could not run.</returns>
    Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of the liveness model: <paramref name="IsLive"/> only when <paramref name="Score"/> reaches the configured threshold.</summary>
/// <param name="IsLive">True when the selfie is judged a live capture.</param>
/// <param name="Score">Score of the real-face class, 0 to 1; 0 when the model did not run.</param>
public readonly record struct LivenessResult(bool IsLive, double Score);
