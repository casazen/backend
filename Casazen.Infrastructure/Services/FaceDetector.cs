using Casazen.Core.Options;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A face the detector found: its box and five landmarks in pixels of the picture it was found in, the detector score and the share of the
/// picture the box covers. Numbers only (ADR-013-R2): the pixels are not kept.
/// </summary>
/// <param name="X">Left of the box.</param>
/// <param name="Y">Top of the box.</param>
/// <param name="Width">Width of the box.</param>
/// <param name="Height">Height of the box.</param>
/// <param name="Score">Confidence of the detector, 0 to 1.</param>
/// <param name="Landmarks">Ten numbers: x and y of the right eye, the left eye, the nose tip, the right and the left corner of the mouth ("right" is the right of the person, so the left side of the picture).</param>
/// <param name="AreaRatio">Area of the box over the area of the picture, 0 to 1.</param>
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score, float[] Landmarks, double AreaRatio);

/// <summary>How many faces a picture has and how much of it the largest covers: what the capture guidance needs, without the faces.</summary>
public sealed record FaceDetectionSummary(int FaceCount, double LargestAreaRatio);

/// <summary>Finds the faces of a picture (E03/F04/US01/T01).</summary>
public interface IFaceDetector
{
    /// <summary>The faces whose score reaches <paramref name="minScore"/>, the largest first. Needs the models: <see cref="BiometricUnavailableException"/> otherwise.</summary>
    IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken);
}

/// <summary>
/// YuNet (MIT, OpenCV Zoo) run with ONNX Runtime: the faces of a picture with five landmarks each (ADR-013, NF-24, NF-51). The picture is scaled to
/// fit <c>Biometrics:DetectorInputSize</c>, padded with black to multiples of 32 and given as blue-green-red values; the twelve outputs (class,
/// objectness, box and landmarks at the strides 8, 16 and 32) are decoded as OpenCV's <c>FaceDetectorYN</c> does, then overlapping boxes are merged.
/// </summary>
public sealed class FaceDetector(IBiometricModelStore store, IOptions<BiometricModelOptions> options) : IFaceDetector
{
    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        var model = store.FaceDetector ?? throw new BiometricUnavailableException();
        var settings = options.Value;
        cancellationToken.ThrowIfCancellationRequested();

        double scale;
        int netWidth;
        int netHeight;
        if (model.InputWidth > 0 && model.InputHeight > 0)
        {
            netWidth = model.InputWidth;
            netHeight = model.InputHeight;
            scale = Math.Min((double)netWidth / image.Width, (double)netHeight / image.Height);
        }
        else
        {
            scale = Math.Min(1.0, (double)settings.DetectorInputSize / Math.Max(image.Width, image.Height));
            netWidth = YuNetDecoder.AlignToStride((int)Math.Ceiling(image.Width * scale));
            netHeight = YuNetDecoder.AlignToStride((int)Math.Ceiling(image.Height * scale));
        }

        var resizedWidth = Math.Clamp((int)Math.Round(image.Width * scale), 1, netWidth);
        var resizedHeight = Math.Clamp((int)Math.Round(image.Height * scale), 1, netHeight);
        RasterImage? resized = null;
        float[]? tensor = null;
        float[][]? outputs = null;
        try
        {
            resized = resizedWidth == image.Width && resizedHeight == image.Height ? null : image.Resized(resizedWidth, resizedHeight);
            tensor = YuNetDecoder.BuildInput(resized ?? image, netWidth, netHeight);
            outputs = Run(model, tensor, netHeight, netWidth, cancellationToken);
            var raw = YuNetDecoder.Decode(outputs, netWidth, netHeight, minScore, settings.NmsIouThreshold);
            return YuNetDecoder.ToPicture(raw, scale, image.Width, image.Height);
        }
        finally
        {
            resized?.Dispose();
            ImageBytes.Zero(tensor);
            if (outputs is not null)
            {
                foreach (var output in outputs)
                    ImageBytes.Zero(output);
            }
        }
    }

    /// <summary>The count of faces and the share of the picture the largest covers, from the bytes of an upload (zeroed before returning).</summary>
    public async Task<FaceDetectionSummary> DetectAsync(Stream imageStream, double minScore, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        byte[]? bytes = null;
        RasterImage? image = null;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(imageStream, settings.MaxImageBytes, cancellationToken);
            image = RasterImage.Decode(bytes, settings.MaxImagePixels);
            ImageBytes.Zero(bytes);
            var faces = Detect(image, minScore, cancellationToken);
            return new FaceDetectionSummary(faces.Count, faces.Count == 0 ? 0 : faces[0].AreaRatio);
        }
        finally
        {
            ImageBytes.Zero(bytes);
            image?.Dispose();
        }
    }

    private static float[][] Run(LoadedBiometricModel model, float[] tensor, int netHeight, int netWidth, CancellationToken cancellationToken)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, new long[] { 1, 3, netHeight, netWidth });
        var inputs = new Dictionary<string, OrtValue> { [model.InputName] = input };
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            using var results = model.Session.Run(runOptions, inputs, BiometricModelStore.RequiredDetectorOutputs);
            var arrays = new float[results.Count][];
            for (var i = 0; i < arrays.Length; i++)
                arrays[i] = results[i].GetTensorDataAsSpan<float>().ToArray();

            return arrays;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

/// <summary>The arithmetic of YuNet: input layout, decoding of the twelve outputs, merging of boxes, return to the coordinates of the picture.</summary>
internal static class YuNetDecoder
{
    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>A candidate in the coordinates of the network input.</summary>
    internal sealed record Raw(float X, float Y, float Width, float Height, float Score, float[] Landmarks);

    public static int AlignToStride(int size) => Math.Max(32, (size + 31) / 32 * 32);

    /// <summary>Planar blue, green, red, 0 to 255 (YuNet takes unnormalised values); the part of the input the picture does not cover is black.</summary>
    public static float[] BuildInput(RasterImage image, int netWidth, int netHeight)
    {
        var plane = netWidth * netHeight;
        var tensor = new float[3 * plane];
        for (var y = 0; y < image.Height && y < netHeight; y++)
        {
            for (var x = 0; x < image.Width && x < netWidth; x++)
            {
                var source = (y * image.Width + x) * 3;
                var target = y * netWidth + x;
                tensor[target] = image.Rgb[source + 2];
                tensor[plane + target] = image.Rgb[source + 1];
                tensor[(2 * plane) + target] = image.Rgb[source];
            }
        }

        return tensor;
    }

    /// <param name="outputs">The twelve tensors in the order of <see cref="BiometricModelStore.RequiredDetectorOutputs"/>: class, objectness, box, landmarks, each at stride 8, 16, 32.</param>
    public static List<Raw> Decode(IReadOnlyList<float[]> outputs, int netWidth, int netHeight, double minScore, double nmsIou)
    {
        if (outputs.Count != 12)
            throw new InvalidDataException("The face detector did not return the expected outputs.");

        var candidates = new List<Raw>();
        for (var s = 0; s < Strides.Length; s++)
        {
            var stride = Strides[s];
            var columns = netWidth / stride;
            var rows = netHeight / stride;
            var cells = rows * columns;
            var cls = outputs[s];
            var obj = outputs[3 + s];
            var box = outputs[6 + s];
            var points = outputs[9 + s];
            if (cls.Length != cells || obj.Length != cells || box.Length != cells * 4 || points.Length != cells * 10)
                throw new InvalidDataException("The face detector outputs do not match its input size.");

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var index = row * columns + column;
                    var score = MathF.Sqrt(Math.Clamp(cls[index], 0f, 1f) * Math.Clamp(obj[index], 0f, 1f));
                    if (score < minScore)
                        continue;

                    var centreX = (column + box[index * 4]) * stride;
                    var centreY = (row + box[(index * 4) + 1]) * stride;
                    var width = MathF.Exp(box[(index * 4) + 2]) * stride;
                    var height = MathF.Exp(box[(index * 4) + 3]) * stride;
                    var landmarks = new float[10];
                    for (var n = 0; n < 5; n++)
                    {
                        landmarks[2 * n] = (points[(index * 10) + (2 * n)] + column) * stride;
                        landmarks[(2 * n) + 1] = (points[(index * 10) + (2 * n) + 1] + row) * stride;
                    }

                    candidates.Add(new Raw(centreX - (width / 2), centreY - (height / 2), width, height, score, landmarks));
                }
            }
        }

        return NonMaxSuppression(candidates.OrderByDescending(c => c.Score).Take(300).ToList(), nmsIou);
    }

    /// <summary>Keeps the best box of every group of boxes that overlap by more than <paramref name="iouThreshold"/> (intersection over union).</summary>
    public static List<Raw> NonMaxSuppression(List<Raw> sortedByScore, double iouThreshold)
    {
        var kept = new List<Raw>();
        foreach (var candidate in sortedByScore)
        {
            if (!kept.Any(other => IntersectionOverUnion(candidate, other) > iouThreshold))
                kept.Add(candidate);
        }

        return kept;
    }

    public static double IntersectionOverUnion(Raw a, Raw b)
    {
        var width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (width <= 0 || height <= 0)
            return 0;

        double intersection = (double)width * height;
        double union = ((double)a.Width * a.Height) + ((double)b.Width * b.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>Back to the pixels of the picture (divides by <paramref name="scale"/>), the box cut to the picture; the largest face first.</summary>
    public static List<DetectedFace> ToPicture(List<Raw> faces, double scale, int imageWidth, int imageHeight)
    {
        var result = new List<DetectedFace>();
        foreach (var face in faces)
        {
            var left = Math.Clamp(face.X / scale, 0, imageWidth);
            var top = Math.Clamp(face.Y / scale, 0, imageHeight);
            var right = Math.Clamp((face.X + face.Width) / scale, 0, imageWidth);
            var bottom = Math.Clamp((face.Y + face.Height) / scale, 0, imageHeight);
            if (right - left < 2 || bottom - top < 2)
                continue;

            var landmarks = new float[10];
            for (var i = 0; i < 10; i++)
                landmarks[i] = (float)(face.Landmarks[i] / scale);

            var ratio = (right - left) * (bottom - top) / ((double)imageWidth * imageHeight);
            result.Add(new DetectedFace((float)left, (float)top, (float)(right - left), (float)(bottom - top), face.Score, landmarks, ratio));
        }

        return result.OrderByDescending(f => f.AreaRatio).ToList();
    }
}
