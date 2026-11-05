using System.IO.Compression;
using System.Text;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>PNG pictures built in memory, so the decoder of the application is proved on bytes the test knows pixel by pixel.</summary>
internal static class TestImages
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var crc = (uint)n;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;

        return crc;
    }).ToArray();

    /// <summary>Red, green, blue for every pixel, from a seed: the same pixels every time.</summary>
    public static byte[] RandomRgb(int width, int height, int seed)
    {
        var rgb = new byte[width * height * 3];
        new Random(seed).NextBytes(rgb);
        return rgb;
    }

    /// <summary>A PNG (8 bits, RGB, no interlace) of the given pixels.</summary>
    public static byte[] Png(int width, int height, byte[] rgb)
    {
        var scanlines = new byte[height * ((width * 3) + 1)];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgb, y * width * 3, scanlines, (y * ((width * 3) + 1)) + 1, width * 3);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(scanlines, 0, scanlines.Length);

        var header = new List<byte>();
        header.AddRange(BigEndian(width));
        header.AddRange(BigEndian(height));
        header.AddRange(new byte[] { 8, 2, 0, 0, 0 });

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header.ToArray());
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A PNG of random pixels.</summary>
    public static byte[] Png(int width, int height, int seed) => Png(width, height, RandomRgb(width, height, seed));

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(BigEndian(data.Length));
        stream.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        stream.Write(BigEndian((int)(crc ^ 0xFFFFFFFFu)));
    }
}

/// <summary>A model store that is ready (or not) without any model behind it: the verification only asks whether the models are available.</summary>
internal sealed class FakeBiometricModelStore(bool available = true) : IBiometricModelStore
{
    public BiometricModelsSnapshot Snapshot { get; } = new(
        available ? BiometricModelsState.Ready : BiometricModelsState.Unavailable,
        available ? null : "file_missing",
        []);

    public bool IsAvailable => available;

    public LoadedBiometricModel? FaceDetector => null;

    public LoadedBiometricModel? AntiSpoofing => null;

    public LoadedBiometricModel? FaceRecognition => null;
}

/// <summary>A detector that answers from a script, keeps the pixel arrays it was shown (to prove they are zeroed afterwards) and can fail or wait.</summary>
internal sealed class ScriptedFaceDetector(params IReadOnlyList<DetectedFace>[] answers) : IFaceDetector
{
    private readonly Queue<IReadOnlyList<DetectedFace>> _answers = new(answers);

    /// <summary>The pixel arrays of the pictures it was shown, as the application held them.</summary>
    public List<byte[]> SeenPixels { get; } = [];

    public Exception? Failure { get; set; }

    public bool WaitForCancellation { get; set; }

    public int Calls { get; private set; }

    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        Calls++;
        SeenPixels.Add(image.Rgb);
        if (WaitForCancellation)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Failure is not null)
            throw Failure;

        return _answers.Dequeue();
    }
}

/// <summary>An embedder that hands out the vectors of a script and keeps them, to prove the application zeroes them after the comparison.</summary>
internal sealed class ScriptedFaceEmbedder(params float[][] vectors) : IFaceEmbedder
{
    private readonly Queue<float[]> _vectors = new(vectors);

    /// <summary>The vectors it handed out (the same arrays the application held).</summary>
    public List<float[]> Issued { get; } = [];

    /// <summary>Raised on the n-th call (1-based) instead of answering.</summary>
    public (int Call, Exception Error)? FailOn { get; set; }

    public int Calls { get; private set; }

    public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        Calls++;
        if (FailOn is { } failure && failure.Call == Calls)
            throw failure.Error;

        var vector = _vectors.Dequeue();
        Issued.Add(vector);
        return vector;
    }
}
