namespace Casazen.Core.Services;

/// <summary>
/// Anti-spoofing detection for biometric verification (ADR-013, NF-11).
/// Uses MiniFASNet (Apache-2.0) to detect photo-of-photo attacks.
/// </summary>
public interface IAntiSpoofingService
{
    /// <summary>
    /// Detects if the selfie image is a live capture or a photo-of-photo attack.
    /// </summary>
    /// <param name="imageStream">The selfie image stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the image appears to be a live capture, false if likely spoofed.</returns>
    Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);
}
