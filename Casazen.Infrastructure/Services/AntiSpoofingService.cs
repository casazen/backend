using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Anti-spoofing detection using MiniFASNet (Apache-2.0) for photo-of-photo and screen attack detection (ADR-013, NF-11, NF-24).
/// Processes images in-memory only with immediate deletion (ADR-013-R2). <b>No code path returns "live" without the model having run</b>: the
/// models not available, no face in the picture, a timeout and an exception are all "not live".
/// </summary>
public class AntiSpoofingService(
    ILogger<AntiSpoofingService> logger,
    IBiometricModelStore? store = null,
    IFaceDetector? detector = null,
    IOptions<BiometricModelOptions>? options = null) : IAntiSpoofingService
{
    /// <summary>
    /// Detects if the selfie image is a live capture or a photo-of-photo attack using MiniFASNet.
    /// </summary>
    public async Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        (await ScoreLivenessAsync(imageStream, cancellationToken)).IsLive;

    /// <summary>
    /// Finds the face of the selfie, cuts it with the margin the model was trained with, and takes the score of the real-face class
    /// (<c>Biometrics:LivenessThreshold</c> decides). The picture, the cut and the tensor are zeroed before returning.
    /// </summary>
    public async Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var settings = options?.Value ?? new BiometricModelOptions();
        byte[]? bytes = null;
        RasterImage? image = null;
        RasterImage? cut = null;
        RasterImage? small = null;
        float[]? input = null;
        try
        {
            var model = store?.AntiSpoofing;
            if (store is not { IsAvailable: true } || model is null || detector is null)
            {
                logger.LogInformation("Liveness check not run: the biometric models are not available");
                return default;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.LivenessTimeoutMs);

            bytes = await ImageBytes.ReadAllAsync(imageStream, settings.MaxImageBytes, timeout.Token);
            image = RasterImage.Decode(bytes, settings.MaxImagePixels);
            ImageBytes.Zero(bytes);

            var faces = detector.Detect(image, settings.DetectionScoreThreshold, timeout.Token);
            if (faces.Count == 0)
            {
                logger.LogInformation("Liveness check: no face in the picture, not live");
                return default;
            }

            var box = LivenessCrop.Box(image.Width, image.Height, faces[0], settings.LivenessCropScale);
            cut = image.Cropped(box.Left, box.Top, box.Right - box.Left + 1, box.Bottom - box.Top + 1);
            small = cut.Resized(settings.LivenessInputSize, settings.LivenessInputSize);
            input = LivenessCrop.BuildInput(small);

            var score = Run(model, input, settings.LivenessInputSize, timeout.Token);
            var live = score >= settings.LivenessThreshold;
            logger.LogInformation("Liveness check: live={IsLive} score={Score:F3}", live, score);
            return new LivenessResult(live, score);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Liveness check timed out, not live");
            return default;
        }
        catch (Exception ex)
        {
            // On error, treat as suspicious (not live) for security. The type only: a message could carry what was being read.
            logger.LogError("Anti-spoofing detection failed ({ErrorType}), not live", ex.GetType().Name);
            return default;
        }
        finally
        {
            // ADR-013-R2: Immediate deletion guarantee
            ImageBytes.Zero(bytes);
            ImageBytes.Zero(input);
            small?.Dispose();
            cut?.Dispose();
            image?.Dispose();
        }
    }

    private static double Run(LoadedBiometricModel model, float[] input, int size, CancellationToken cancellationToken)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, size, size });
        var inputs = new Dictionary<string, OrtValue> { [model.InputName] = value };
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            using var results = model.Session.Run(runOptions, inputs, new[] { model.Session.OutputNames[0] });
            return LivenessCrop.RealFaceScore(results[0].GetTensorDataAsSpan<float>());
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

/// <summary>The arithmetic around MiniFASNet: where the face is cut, how the cut is given to the model and how its answer becomes a score.</summary>
internal static class LivenessCrop
{
    /// <summary>
    /// The box of the face made <paramref name="scale"/> times larger around its centre and moved (not cut) to stay inside the picture, as the
    /// authors of the model do. Inclusive pixel coordinates.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) Box(int imageWidth, int imageHeight, DetectedFace face, double scale)
    {
        double boxWidth = Math.Max(1.0, face.Width);
        double boxHeight = Math.Max(1.0, face.Height);
        var factor = Math.Min((imageHeight - 1.0) / boxHeight, Math.Min((imageWidth - 1.0) / boxWidth, scale));
        var newWidth = boxWidth * factor;
        var newHeight = boxHeight * factor;
        var centreX = face.X + (boxWidth / 2);
        var centreY = face.Y + (boxHeight / 2);
        var left = centreX - (newWidth / 2);
        var top = centreY - (newHeight / 2);
        var right = centreX + (newWidth / 2);
        var bottom = centreY + (newHeight / 2);
        if (left < 0)
        {
            right -= left;
            left = 0;
        }

        if (top < 0)
        {
            bottom -= top;
            top = 0;
        }

        if (right > imageWidth - 1)
        {
            left -= right - imageWidth + 1;
            right = imageWidth - 1;
        }

        if (bottom > imageHeight - 1)
        {
            top -= bottom - imageHeight + 1;
            bottom = imageHeight - 1;
        }

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        return ((int)left, (int)top, Math.Max((int)left + 1, (int)right), Math.Max((int)top + 1, (int)bottom));
    }

    /// <summary>Planar blue, green, red, 0 to 255 (the model takes unnormalised values, in the order OpenCV reads a picture).</summary>
    public static float[] BuildInput(RasterImage cut)
    {
        var plane = cut.Width * cut.Height;
        var tensor = new float[3 * plane];
        for (var i = 0; i < plane; i++)
        {
            tensor[i] = cut.Rgb[(i * 3) + 2];
            tensor[plane + i] = cut.Rgb[(i * 3) + 1];
            tensor[(2 * plane) + i] = cut.Rgb[i * 3];
        }

        return tensor;
    }

    /// <summary>
    /// The score of the real-face class of the three the model returns (0 = print attack, 1 = real face, 2 = screen attack): the answer as
    /// probabilities when it already is, otherwise the softmax of the logits.
    /// </summary>
    public static double RealFaceScore(ReadOnlySpan<float> output)
    {
        if (output.Length != 3)
            throw new InvalidDataException("The anti-spoofing model did not return three classes.");

        var probabilities = output[0] >= 0 && output[1] >= 0 && output[2] >= 0 && Math.Abs((output[0] + output[1] + output[2]) - 1f) < 1e-3f;
        if (probabilities)
            return Math.Clamp(output[1], 0f, 1f);

        var max = Math.Max(output[0], Math.Max(output[1], output[2]));
        var e0 = Math.Exp(output[0] - max);
        var e1 = Math.Exp(output[1] - max);
        var e2 = Math.Exp(output[2] - max);
        var score = e1 / (e0 + e1 + e2);
        return double.IsNaN(score) ? 0 : Math.Clamp(score, 0.0, 1.0);
    }
}
