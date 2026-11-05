using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T01: the arithmetic around the models, proved without the models: the decoding of the detector outputs, the merging of boxes, the
/// alignment on the landmarks, the score of the liveness classes, the vectors and the pictures.
/// </summary>
public class FaceGeometryTests
{
    private static readonly float[] TemplateLandmarks =
    [
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f,
    ];

    /// <summary>The twelve outputs of YuNet for an input of 64 x 64: strides 8, 16 and 32 give 64, 16 and 4 cells.</summary>
    private static float[][] EmptyOutputs() =>
    [
        new float[64], new float[16], new float[4],
        new float[64], new float[16], new float[4],
        new float[64 * 4], new float[16 * 4], new float[4 * 4],
        new float[64 * 10], new float[16 * 10], new float[4 * 10],
    ];

    [Fact]
    public void YuNet_DecodesTheBoxScoreAndLandmarksOfACell()
    {
        // Stride 16, row 1, column 2: the centre is ((2 + 0.5) * 16, (1 + 0.5) * 16) = (40, 24), the size exp(ln 2) * 16 = 32.
        var outputs = EmptyOutputs();
        const int cell = (1 * 4) + 2;
        outputs[1][cell] = 0.9f;
        outputs[4][cell] = 0.9f;
        outputs[7][(cell * 4) + 0] = 0.5f;
        outputs[7][(cell * 4) + 1] = 0.5f;
        outputs[7][(cell * 4) + 2] = MathF.Log(2);
        outputs[7][(cell * 4) + 3] = MathF.Log(2);
        outputs[10][(cell * 10) + 0] = 0.5f;
        outputs[10][(cell * 10) + 1] = 0.5f;

        var faces = YuNetDecoder.Decode(outputs, 64, 64, minScore: 0.5, nmsIou: 0.3);

        var face = Assert.Single(faces);
        Assert.Equal(24f, face.X, 0.01f);
        Assert.Equal(8f, face.Y, 0.01f);
        Assert.Equal(32f, face.Width, 0.01f);
        Assert.Equal(32f, face.Height, 0.01f);
        Assert.Equal(0.9f, face.Score, 0.001f);
        Assert.Equal(40f, face.Landmarks[0], 0.01f);
        Assert.Equal(24f, face.Landmarks[1], 0.01f);
    }

    [Fact]
    public void YuNet_ScoreUnderTheMinimum_GivesNoFace()
    {
        var outputs = EmptyOutputs();
        outputs[0][10] = 0.5f;
        outputs[3][10] = 0.5f;

        Assert.Empty(YuNetDecoder.Decode(outputs, 64, 64, minScore: 0.8, nmsIou: 0.3));
    }

    [Fact]
    public void YuNet_OutputsThatDoNotMatchTheInputSize_AreRefused()
    {
        var outputs = EmptyOutputs();
        outputs[0] = new float[10];

        Assert.Throws<InvalidDataException>(() => YuNetDecoder.Decode(outputs, 64, 64, 0.5, 0.3));
        Assert.Throws<InvalidDataException>(() => YuNetDecoder.Decode([new float[1]], 64, 64, 0.5, 0.3));
    }

    [Fact]
    public void YuNet_BoxesThatOverlapAreOneFace()
    {
        var best = new YuNetDecoder.Raw(0, 0, 10, 10, 0.9f, new float[10]);
        var duplicate = new YuNetDecoder.Raw(1, 1, 10, 10, 0.8f, new float[10]);
        var other = new YuNetDecoder.Raw(50, 50, 10, 10, 0.7f, new float[10]);

        var kept = YuNetDecoder.NonMaxSuppression([best, duplicate, other], 0.3);

        Assert.Equal(2, kept.Count);
        Assert.Same(best, kept[0]);
        Assert.Same(other, kept[1]);
        Assert.Equal(81.0 / 119.0, YuNetDecoder.IntersectionOverUnion(best, duplicate), 6);
        Assert.Equal(0.0, YuNetDecoder.IntersectionOverUnion(best, other));
    }

    [Fact]
    public void YuNet_BoxesGoBackToThePixelsOfThePicture_CutToIt()
    {
        var inside = new YuNetDecoder.Raw(24, 8, 32, 32, 0.9f, new float[10]);
        var atTheEdge = new YuNetDecoder.Raw(90, 40, 40, 40, 0.9f, new float[10]);
        var tooSmall = new YuNetDecoder.Raw(10, 10, 0.5f, 0.5f, 0.9f, new float[10]);

        var faces = YuNetDecoder.ToPicture([inside, atTheEdge, tooSmall], scale: 0.5, imageWidth: 200, imageHeight: 100);

        // Scale 0.5: the box is twice as large in the picture. The second one is cut at the right and bottom edge. The third is dropped.
        Assert.Equal(2, faces.Count);
        Assert.Equal(48f, faces[0].X, 0.01f);
        Assert.Equal(16f, faces[0].Y, 0.01f);
        Assert.Equal(64f, faces[0].Width, 0.01f);
        Assert.Equal(0.2048, faces[0].AreaRatio, 6);
        Assert.Equal(180f, faces[1].X, 0.01f);
        Assert.Equal(200f, faces[1].X + faces[1].Width, 0.01f);
        Assert.Equal(100f, faces[1].Y + faces[1].Height, 0.01f);
        Assert.Equal(0.02, faces[1].AreaRatio, 6);
        Assert.True(faces[0].AreaRatio >= faces[1].AreaRatio, "the largest face first");
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(640, 640)]
    public void YuNet_InputSizeIsAMultipleOf32(int size, int expected) => Assert.Equal(expected, YuNetDecoder.AlignToStride(size));

    [Fact]
    public void YuNet_InputIsBlueGreenRedAndBlackWhereThePictureDoesNotReach()
    {
        // A 2 x 1 picture: pixel 0 is (10, 20, 30), pixel 1 is (40, 50, 60); the input is 4 x 2.
        using var image = new RasterImage(2, 1, [10, 20, 30, 40, 50, 60]);

        var tensor = YuNetDecoder.BuildInput(image, 4, 2);

        Assert.Equal(24, tensor.Length);
        Assert.Equal(new[] { 30f, 60f, 0f, 0f, 0f, 0f, 0f, 0f }, tensor[..8]);
        Assert.Equal(new[] { 20f, 50f, 0f, 0f, 0f, 0f, 0f, 0f }, tensor[8..16]);
        Assert.Equal(new[] { 10f, 40f, 0f, 0f, 0f, 0f, 0f, 0f }, tensor[16..]);
    }

    [Fact]
    public void Alignment_LandmarksAtTheTemplate_AreTheIdentity()
    {
        Assert.True(FaceAlignment.TryEstimate(TemplateLandmarks, 112, out var a, out var b, out var tx, out var ty));

        Assert.Equal(1.0, a, 5);
        Assert.Equal(0.0, b, 5);
        Assert.Equal(0.0, tx, 4);
        Assert.Equal(0.0, ty, 4);
    }

    [Fact]
    public void Alignment_ScaledAndShiftedLandmarks_AreBroughtBack()
    {
        // The face is twice as large and 10 pixels right, 20 down: the transform halves it and moves it back.
        var landmarks = TemplateLandmarks.Select((v, i) => (v * 2f) + (i % 2 == 0 ? 10f : 20f)).ToArray();

        Assert.True(FaceAlignment.TryEstimate(landmarks, 112, out var a, out var b, out var tx, out var ty));

        Assert.Equal(0.5, a, 5);
        Assert.Equal(0.0, b, 5);
        Assert.Equal(-5.0, tx, 4);
        Assert.Equal(-10.0, ty, 4);
    }

    [Fact]
    public void Alignment_RotatedLandmarks_AreRotatedBack()
    {
        var angle = 30 * Math.PI / 180;
        var landmarks = new float[10];
        for (var i = 0; i < 5; i++)
        {
            var x = TemplateLandmarks[2 * i];
            var y = TemplateLandmarks[(2 * i) + 1];
            landmarks[2 * i] = (float)((Math.Cos(angle) * x) - (Math.Sin(angle) * y));
            landmarks[(2 * i) + 1] = (float)((Math.Sin(angle) * x) + (Math.Cos(angle) * y));
        }

        Assert.True(FaceAlignment.TryEstimate(landmarks, 112, out var a, out var b, out var tx, out var ty));

        Assert.Equal(Math.Cos(-angle), a, 4);
        Assert.Equal(Math.Sin(-angle), b, 4);
        Assert.Equal(0.0, tx, 2);
        Assert.Equal(0.0, ty, 2);
    }

    [Fact]
    public void Alignment_LandmarksInOnePlace_CannotBeAligned()
    {
        Assert.False(FaceAlignment.TryEstimate(new float[10], 112, out _, out _, out _, out _));
        Assert.Null(FaceAlignment.AlignToTensor(new RasterImage(4, 4, new byte[48]), new float[10], 112, 0, 1, rgbOrder: true));
    }

    [Fact]
    public void Alignment_FaceAtTheTemplate_IsCopiedPixelForPixel()
    {
        // A 112 x 112 picture whose red is the column, green the row and blue 77: with the landmarks at the template nothing moves.
        var rgb = new byte[112 * 112 * 3];
        for (var y = 0; y < 112; y++)
        {
            for (var x = 0; x < 112; x++)
            {
                rgb[((y * 112) + x) * 3] = (byte)x;
                rgb[(((y * 112) + x) * 3) + 1] = (byte)y;
                rgb[(((y * 112) + x) * 3) + 2] = 77;
            }
        }

        using var image = new RasterImage(112, 112, rgb);
        var plane = 112 * 112;
        var at = (50 * 112) + 30;

        var rgbTensor = FaceAlignment.AlignToTensor(image, TemplateLandmarks, 112, mean: 0, std: 1, rgbOrder: true)!;
        var bgrTensor = FaceAlignment.AlignToTensor(image, TemplateLandmarks, 112, mean: 0, std: 1, rgbOrder: false)!;
        var normalised = FaceAlignment.AlignToTensor(image, TemplateLandmarks, 112, mean: 127.5, std: 127.5, rgbOrder: true)!;

        Assert.Equal(3 * plane, rgbTensor.Length);
        Assert.Equal(30f, rgbTensor[at], 0.05f);
        Assert.Equal(50f, rgbTensor[plane + at], 0.05f);
        Assert.Equal(77f, rgbTensor[(2 * plane) + at], 0.05f);
        Assert.Equal(77f, bgrTensor[at], 0.05f);
        Assert.Equal(30f, bgrTensor[(2 * plane) + at], 0.05f);
        Assert.Equal((30f - 127.5f) / 127.5f, normalised[at], 0.001f);
    }

    [Fact]
    public void Liveness_ProbabilitiesAreTakenAsTheyAre_LogitsGoThroughSoftmax()
    {
        Assert.Equal(0.8, LivenessCrop.RealFaceScore([0.1f, 0.8f, 0.1f]), 5);
        Assert.Equal(Math.Exp(2) / (Math.Exp(2) + 2), LivenessCrop.RealFaceScore([0f, 2f, 0f]), 5);
        Assert.True(LivenessCrop.RealFaceScore([5f, -3f, 1f]) < 0.001);
        Assert.Throws<InvalidDataException>(() => LivenessCrop.RealFaceScore([0.5f, 0.5f]));
    }

    [Fact]
    public void Liveness_TheCutIsEnlargedAroundTheFace_AndMovedNotCutAtTheEdge()
    {
        var centred = new DetectedFace(40, 40, 20, 20, 0.99f, new float[10], 0.04);
        var corner = new DetectedFace(0, 0, 20, 20, 0.99f, new float[10], 0.04);

        Assert.Equal((25, 25, 75, 75), LivenessCrop.Box(100, 100, centred, 2.5));
        Assert.Equal((0, 0, 50, 50), LivenessCrop.Box(100, 100, corner, 2.5));
    }

    [Fact]
    public void Liveness_InputIsBlueGreenRedOfTheCut()
    {
        using var cut = new RasterImage(2, 1, [10, 20, 30, 40, 50, 60]);

        var tensor = LivenessCrop.BuildInput(cut);

        Assert.Equal(new[] { 30f, 60f, 20f, 50f, 10f, 40f }, tensor);
    }

    [Fact]
    public void Vectors_AreScaledToLengthOne_AndComparedByCosine()
    {
        var vector = new[] { 3f, 4f };
        EmbeddingMath.Normalize(vector);

        Assert.Equal(0.6f, vector[0], 0.0001f);
        Assert.Equal(0.8f, vector[1], 0.0001f);
        Assert.Equal(1.0, EmbeddingMath.CosineSimilarity([1f, 0f], [2f, 0f]), 6);
        Assert.Equal(0.0, EmbeddingMath.CosineSimilarity([1f, 0f], [0f, 3f]), 6);
        Assert.Equal(-1.0, EmbeddingMath.CosineSimilarity([1f, 0f], [-1f, 0f]), 6);
        Assert.Throws<InvalidDataException>(() => EmbeddingMath.Normalize(new float[4]));
        Assert.Throws<InvalidDataException>(() => EmbeddingMath.CosineSimilarity([1f], [1f, 2f]));
    }

    [Fact]
    public void Picture_Png_IsDecodedToTheSamePixels_AndZeroedByDispose()
    {
        var pixels = TestImages.RandomRgb(20, 12, seed: 5);
        var png = TestImages.Png(20, 12, pixels);

        var image = RasterImage.Decode(png, maxPixels: 1000);
        var held = image.Rgb;

        Assert.Equal((20, 12), (image.Width, image.Height));
        Assert.Equal(pixels, held);

        image.Dispose();

        Assert.All(held, b => Assert.Equal(0, b));
        Assert.Empty(image.Rgb);
    }

    [Fact]
    public void Picture_TooLargeOrNotAPicture_IsRefusedBeforeItIsDecoded()
    {
        var png = TestImages.Png(40, 30, seed: 3);

        Assert.Throws<InvalidDataException>(() => RasterImage.Decode(png, maxPixels: 1000));
        Assert.Throws<InvalidDataException>(() => RasterImage.Decode([1, 2, 3, 4], maxPixels: 1_000_000));
    }

    [Fact]
    public void Picture_Orientation_TurnsThePixelsAsExifSays()
    {
        // 3 x 2: red is 0 1 2 / 10 11 12.
        byte[] rgb = [0, 0, 0, 1, 0, 0, 2, 0, 0, 10, 0, 0, 11, 0, 0, 12, 0, 0];
        using var image = new RasterImage(3, 2, rgb);

        using var clockwise = image.Oriented(6);
        using var counterClockwise = image.Oriented(8);
        using var half = image.Oriented(3);
        using var mirrored = image.Oriented(2);
        using var same = image.Oriented(1);

        Assert.Equal((2, 3), (clockwise.Width, clockwise.Height));
        Assert.Equal(new[] { 10, 0, 11, 1, 12, 2 }, Reds(clockwise));
        Assert.Equal((2, 3), (counterClockwise.Width, counterClockwise.Height));
        Assert.Equal(new[] { 2, 12, 1, 11, 0, 10 }, Reds(counterClockwise));
        Assert.Equal(new[] { 12, 11, 10, 2, 1, 0 }, Reds(half));
        Assert.Equal(new[] { 2, 1, 0, 12, 11, 10 }, Reds(mirrored));
        Assert.Equal(new[] { 0, 1, 2, 10, 11, 12 }, Reds(same));
    }

    private static int[] Reds(RasterImage image) => Enumerable.Range(0, image.Width * image.Height).Select(i => (int)image.Rgb[i * 3]).ToArray();

    [Fact]
    public void Exif_OrientationOfAJpegHeader_IsRead()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x22, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xDA, 0x00, 0x02,
        ];

        Assert.Equal(6, ExifOrientation.Read(jpeg));
        Assert.Equal(1, ExifOrientation.Read(jpeg[..20]));
        Assert.Equal(1, ExifOrientation.Read(TestImages.Png(4, 4, seed: 1)));
        Assert.Equal(1, ExifOrientation.Read([]));
    }

    [Fact]
    public void Resize_Shrinks_ByTheAverageOfThePixelsItCovers_AndGrowsByInterpolation()
    {
        Assert.Equal(new byte[] { 50, 150 }, PixelGrid.Resize(new byte[] { 0, 100, 200, 100 }, 4, 1, 2, 1, 1));
        Assert.Equal(new byte[] { 0, 25, 75, 100 }, PixelGrid.Resize(new byte[] { 0, 100 }, 2, 1, 4, 1, 1));
        Assert.Equal(Enumerable.Repeat((byte)40, 9).ToArray(), PixelGrid.Resize(new byte[] { 40 }, 1, 1, 3, 3, 1));
    }
}
