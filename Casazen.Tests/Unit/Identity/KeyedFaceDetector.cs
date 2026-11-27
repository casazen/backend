using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Identity;

internal sealed class KeyedFaceDetector : IFaceDetector
{
    private readonly Dictionary<RasterImage, IReadOnlyList<DetectedFace>> _faces = new(ReferenceEqualityComparer.Instance);

    public List<(RasterImage Picture, double MinScore)> Calls { get; } = [];

    /// <summary>Raised on every call instead of answering.</summary>
    public Exception? Failure { get; set; }

    public bool WaitForCancellation { get; set; }

    public Func<RasterImage, IReadOnlyList<DetectedFace>>? Fallback { get; set; }

    public void Add(RasterImage picture, params DetectedFace[] faces) => _faces[picture] = faces;

    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        Calls.Add((image, minScore));
        if (WaitForCancellation)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Failure is not null)
            throw Failure;

        if (_faces.TryGetValue(image, out var faces))
            return faces;

        return Fallback?.Invoke(image) ?? [];
    }
}

internal static class TestPictures
{
    public static RasterImage Noise(int width, int height, int seed)
    {
        var gray = TestJpeg.NoisePixels(width, height, seed);
        var rgb = new byte[width * height * 3];
        for (var pixel = 0; pixel < gray.Length; pixel++)
        {
            rgb[pixel * 3] = gray[pixel];
            rgb[(pixel * 3) + 1] = gray[pixel];
            rgb[(pixel * 3) + 2] = gray[pixel];
        }

        return new RasterImage(width, height, rgb);
    }
}
