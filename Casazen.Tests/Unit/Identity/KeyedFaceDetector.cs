using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// A face detector double that knows the faces of the pictures it is shown, picture by picture (the very object: a picture it does not know has none, which is what a copy of a picture made by the
/// application gets), keeps what it was asked (the picture and the minimum score) and can fail or wait (E72/F01/US02). The faces are written in code: no photograph of anybody is in the repository.
/// </summary>
internal sealed class KeyedFaceDetector : IFaceDetector
{
    private readonly Dictionary<RasterImage, IReadOnlyList<DetectedFace>> _faces = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every call, in order: the picture it was shown and the minimum score it was given.</summary>
    public List<(RasterImage Picture, double MinScore)> Calls { get; } = [];

    /// <summary>Raised on every call instead of answering.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Waits (up to 20 seconds) for the token to be cancelled, then raises the cancellation.</summary>
    public bool WaitForCancellation { get; set; }

    /// <summary>The faces of a picture it does not know, when it should find some (a frame the application decoded itself from its bytes is a new object): none when this is null.</summary>
    public Func<RasterImage, IReadOnlyList<DetectedFace>>? Fallback { get; set; }

    /// <summary>The faces of <paramref name="picture"/>, the largest first.</summary>
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

/// <summary>Pictures built in code for the tests of the matching: random gray noise, the same for the same seed.</summary>
internal static class TestPictures
{
    /// <summary>A picture of random gray noise, as a decoded <see cref="RasterImage"/>.</summary>
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
