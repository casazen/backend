using Casazen.Core.Options;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Casazen.Infrastructure.Services;

/// <summary>Turns a face into the vector two faces are compared with (E03/F04/US01/T01).</summary>
public interface IFaceEmbedder
{
    /// <summary>
    /// The vector of one face of a picture: the face is cut and aligned on its landmarks, normalised and given to the recognition model; the
    /// vector is scaled to length 1. The caller zeroes it as soon as it has compared it (ADR-013-R2). Needs the models:
    /// <see cref="BiometricUnavailableException"/> otherwise.
    /// </summary>
    float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken);
}

/// <summary>
/// AuraFace-v1 (Apache-2.0) or SFace, run with ONNX Runtime: crop, align on the five landmarks, normalise, embed (ADR-013, NF-24, NF-51). The
/// crop, the tensor and the output are zeroed before the method returns; only the vector goes back to the caller.
/// </summary>
public sealed class FaceEmbedder(IBiometricModelStore store, IOptions<BiometricModelOptions> options) : IFaceEmbedder
{
    public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        var model = store.FaceRecognition ?? throw new BiometricUnavailableException();
        var settings = options.Value;
        cancellationToken.ThrowIfCancellationRequested();

        var size = model.InputWidth > 0 && model.InputWidth == model.InputHeight ? model.InputWidth : settings.RecognitionInputSize;
        float[]? tensor = null;
        float[]? embedding = null;
        try
        {
            tensor = FaceAlignment.AlignToTensor(
                image, face.Landmarks, size, settings.RecognitionMean, settings.RecognitionStd, settings.RecognitionRgbOrder)
                ?? throw new InvalidDataException("The landmarks of the face cannot be aligned.");

            using var input = OrtValue.CreateTensorValueFromMemory(tensor, new long[] { 1, 3, size, size });
            var inputs = new Dictionary<string, OrtValue> { [model.InputName] = input };
            using var runOptions = new RunOptions();
            using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
            try
            {
                using var results = model.Session.Run(runOptions, inputs, new[] { model.Session.OutputNames[0] });
                embedding = results[0].GetTensorDataAsSpan<float>().ToArray();
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            EmbeddingMath.Normalize(embedding);
            var result = embedding;
            embedding = null;
            return result;
        }
        finally
        {
            ImageBytes.Zero(tensor);
            ImageBytes.Zero(embedding);
        }
    }
}

/// <summary>The similarity of two vectors of length 1.</summary>
internal static class EmbeddingMath
{
    /// <summary>Scales the vector to length 1 in place. A vector with no length is not a face: <see cref="InvalidDataException"/>.</summary>
    public static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
            sum += (double)value * value;

        var norm = Math.Sqrt(sum);
        if (vector.Length == 0 || double.IsNaN(norm) || double.IsInfinity(norm) || norm < 1e-9)
            throw new InvalidDataException("The recognition model returned an unusable vector.");

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    /// <summary>Cosine of the angle between two vectors of the same length; the dot product when both have length 1. -1 to 1.</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
            throw new InvalidDataException("The two vectors cannot be compared.");

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator < 1e-12 ? 0 : Math.Clamp(dot / denominator, -1.0, 1.0);
    }
}

/// <summary>
/// The alignment of a face on the five landmarks (the similarity transform of the ArcFace family): the landmarks YuNet returns go to the fixed
/// positions the recognition models were trained with, in a square of 112 pixels (scaled for other sizes).
/// </summary>
internal static class FaceAlignment
{
    /// <summary>Where the right eye, the left eye, the nose, the right and the left corner of the mouth go in 112 x 112 pixels.</summary>
    private static readonly double[] Template112 =
    [
        38.2946, 51.6963,
        73.5318, 51.5014,
        56.0252, 71.7366,
        41.5493, 92.3655,
        70.7299, 92.2041,
    ];

    /// <summary>
    /// Least-squares similarity (a scale, a rotation, a shift) from the landmarks to the template: x' = a x - b y + tx, y' = b x + a y + ty.
    /// False when the landmarks have no spread (all in the same place).
    /// </summary>
    public static bool TryEstimate(ReadOnlySpan<float> landmarks, int size, out double a, out double b, out double tx, out double ty)
    {
        a = b = tx = ty = 0;
        if (landmarks.Length < 10)
            return false;

        var scale = size / 112.0;
        double meanPx = 0, meanPy = 0, meanQx = 0, meanQy = 0;
        for (var i = 0; i < 5; i++)
        {
            meanPx += landmarks[2 * i];
            meanPy += landmarks[(2 * i) + 1];
            meanQx += Template112[2 * i] * scale;
            meanQy += Template112[(2 * i) + 1] * scale;
        }

        meanPx /= 5;
        meanPy /= 5;
        meanQx /= 5;
        meanQy /= 5;

        double numeratorA = 0, numeratorB = 0, denominator = 0;
        for (var i = 0; i < 5; i++)
        {
            var px = landmarks[2 * i] - meanPx;
            var py = landmarks[(2 * i) + 1] - meanPy;
            var qx = (Template112[2 * i] * scale) - meanQx;
            var qy = (Template112[(2 * i) + 1] * scale) - meanQy;
            numeratorA += (px * qx) + (py * qy);
            numeratorB += (px * qy) - (py * qx);
            denominator += (px * px) + (py * py);
        }

        if (denominator < 1e-6 || double.IsNaN(denominator))
            return false;

        a = numeratorA / denominator;
        b = numeratorB / denominator;
        tx = meanQx - ((a * meanPx) - (b * meanPy));
        ty = meanQy - ((b * meanPx) + (a * meanPy));
        return true;
    }

    /// <summary>
    /// The face of <paramref name="image"/> aligned to a <paramref name="size"/> square, as a planar tensor of <c>(value - mean) / std</c>, in the
    /// colour order the model wants; null when the landmarks cannot be aligned. What the square does not cover of the picture is black.
    /// </summary>
    public static float[]? AlignToTensor(RasterImage image, ReadOnlySpan<float> landmarks, int size, double mean, double std, bool rgbOrder)
    {
        if (!TryEstimate(landmarks, size, out var a, out var b, out var tx, out var ty))
            return null;

        var determinant = (a * a) + (b * b);
        if (determinant < 1e-12)
            return null;

        var plane = size * size;
        var tensor = new float[3 * plane];
        var firstPlane = rgbOrder ? 0 : 2;
        var thirdPlane = rgbOrder ? 2 : 0;
        for (var v = 0; v < size; v++)
        {
            for (var u = 0; u < size; u++)
            {
                var du = u - tx;
                var dv = v - ty;
                var sx = ((a * du) + (b * dv)) / determinant;
                var sy = ((-b * du) + (a * dv)) / determinant;
                var x0 = (int)Math.Floor(sx);
                var y0 = (int)Math.Floor(sy);
                var fx = sx - x0;
                var fy = sy - y0;
                double red = 0, green = 0, blue = 0;
                AddPixel(image, x0, y0, (1 - fx) * (1 - fy), ref red, ref green, ref blue);
                AddPixel(image, x0 + 1, y0, fx * (1 - fy), ref red, ref green, ref blue);
                AddPixel(image, x0, y0 + 1, (1 - fx) * fy, ref red, ref green, ref blue);
                AddPixel(image, x0 + 1, y0 + 1, fx * fy, ref red, ref green, ref blue);

                var index = (v * size) + u;
                tensor[(firstPlane * plane) + index] = (float)((red - mean) / std);
                tensor[plane + index] = (float)((green - mean) / std);
                tensor[(thirdPlane * plane) + index] = (float)((blue - mean) / std);
            }
        }

        return tensor;
    }

    private static void AddPixel(RasterImage image, int x, int y, double weight, ref double red, ref double green, ref double blue)
    {
        if (weight <= 0 || x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            return;

        var at = ((y * image.Width) + x) * 3;
        red += image.Rgb[at] * weight;
        green += image.Rgb[at + 1] * weight;
        blue += image.Rgb[at + 2] * weight;
    }
}
