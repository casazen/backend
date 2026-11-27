using System.Runtime.InteropServices;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US01/T02 (NF-192, ADR-013-R2, R17, R22): the frames of a presence sequence are JPEG parts with timestamps that the server decodes in memory and holds only for
/// the request. Each limit has its code (<c>presence_sequence_invalid</c> or <c>presence_sequence_too_large</c>), the first violation stops the intake and zeroes what was
/// read, and a sequence that is disposed leaves no frame behind. The pictures are JPEG written by <see cref="TestJpeg"/> and read by the decoder of the application.
/// </summary>
public class FrameSequenceReaderTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_AValidSequence_GivesTheFramesWithTheirTimestampsDurationAndDecodedPictures()
    {
        var first = TestJpeg.NoisePixels(32, 24, 1);
        byte[][] jpegs = [TestJpeg.Gray(32, 24, first), TestJpeg.Noise(32, 24, 2), TestJpeg.Noise(32, 24, 3)];

        var result = await Reader().ReadAsync([.. jpegs.Select(jpeg => Part(jpeg))], "[0, 125, 300]", Received, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        using var sequence = result.Sequence!;
        Assert.Equal([0, 1, 2], sequence.Frames.Select(frame => frame.Index));
        Assert.Equal([0L, 125L, 300L], sequence.Frames.Select(frame => frame.TimestampMs));
        Assert.Equal(300, sequence.DurationMs);
        Assert.Equal(Received, sequence.ReceivedAt);
        Assert.Equal(jpegs, sequence.Frames.Select(frame => frame.Jpeg));
        Assert.All(sequence.Frames, frame =>
        {
            Assert.Equal((32, 24), (frame.Image.Width, frame.Image.Height));
            Assert.Equal(32 * 24 * 3, frame.Image.Rgb.Length);
        });

        // The picture is the one that was sent: a gray one (red, green and blue agree) close to the pixels the test wrote.
        var rgb = sequence.Frames[0].Image.Rgb;
        double difference = 0;
        for (var pixel = 0; pixel < first.Length; pixel++)
        {
            Assert.Equal(rgb[pixel * 3], rgb[(pixel * 3) + 1]);
            Assert.Equal(rgb[pixel * 3], rgb[(pixel * 3) + 2]);
            difference += Math.Abs(rgb[pixel * 3] - first[pixel]);
        }

        Assert.True(difference / first.Length < 6, "The decoded picture is not the one that was sent.");
    }

    [Fact]
    public async Task ReadAsync_TheDeclaredTypeDecidesNothing_TheBytesDo()
    {
        byte[] jpeg = TestJpeg.Noise(16, 16, 4);
        var parts = new List<FramePart> { new(new MemoryStream(jpeg), jpeg.Length, "text/plain"), new(new MemoryStream(jpeg), jpeg.Length, null) };

        var result = await Reader().ReadAsync(parts, "[0,100]", Received, CancellationToken.None);

        Assert.True(result.Succeeded);
        result.Sequence!.Dispose();
    }

    [Fact]
    public async Task ReadAsync_NoFrameAtAll_IsInvalid()
    {
        var result = await Reader().ReadAsync([], "[]", Received, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
        Assert.Null(result.Sequence);
    }

    [Fact]
    public async Task ReadAsync_MoreFramesThanTheLimit_IsTooLarge_AndNothingIsRead()
    {
        var streams = Enumerable.Range(0, 3).Select(seed => new RecordingStream(TestJpeg.Noise(16, 16, seed))).ToList();
        var reader = Reader(new IdentityPresenceOptions { MaxFrames = 2 });

        var result = await reader.ReadAsync([.. streams.Select(stream => Part(stream))], "[0,100,200]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceTooLarge, result.ErrorCode);
        Assert.All(streams, stream => Assert.Equal(0, stream.Reads));
    }

    [Fact]
    public async Task ReadAsync_OnePartOverTheLimit_IsTooLarge_AndNothingIsRead()
    {
        var small = TestJpeg.Flat(8, 8);
        var big = TestJpeg.Noise(64, 48, 5);
        Assert.True(big.Length > small.Length + 10);
        var streams = new[] { new RecordingStream(small), new RecordingStream(big) };
        var reader = Reader(new IdentityPresenceOptions { MaxFrameBytes = small.Length + 10 });

        var result = await reader.ReadAsync([.. streams.Select(stream => Part(stream))], "[0,100]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceTooLarge, result.ErrorCode);
        Assert.All(streams, stream => Assert.Equal(0, stream.Reads));
    }

    [Fact]
    public async Task ReadAsync_AllThePartsOverTheLimit_IsTooLarge_AndNothingIsRead()
    {
        var jpegs = new[] { TestJpeg.Noise(32, 24, 6), TestJpeg.Noise(32, 24, 7) };
        var streams = jpegs.Select(jpeg => new RecordingStream(jpeg)).ToList();
        var reader = Reader(new IdentityPresenceOptions { MaxSequenceBytes = jpegs.Sum(jpeg => jpeg.Length) - 1 });

        var result = await reader.ReadAsync([.. streams.Select(stream => Part(stream))], "[0,100]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceTooLarge, result.ErrorCode);
        Assert.All(streams, stream => Assert.Equal(0, stream.Reads));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("random")]
    [InlineData("two bytes of a jpeg")]
    [InlineData("a jpeg that begins and goes nowhere")]
    public async Task ReadAsync_APartThatIsNotAJpeg_DeclaredAsOne_IsInvalid(string kind)
    {
        byte[] content = kind switch
        {
            "png" => TestImages.Png(8, 8, seed: 1),
            "random" => [.. Enumerable.Range(0, 200).Select(value => (byte)((value * 37) + 11))],
            "two bytes of a jpeg" => [0xFF, 0xD8],
            _ => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9],
        };
        var valid = TestJpeg.Noise(16, 16, 8);

        var result = await Reader().ReadAsync([Part(valid), Part(content)], "[0,100]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
    }

    [Theory]
    [InlineData("[0,100,100]")]
    [InlineData("[0,200,100]")]
    [InlineData("[0,100]")]
    [InlineData("[0,100,200,300]")]
    [InlineData("[0,-5,10]")]
    [InlineData("[5,10,15]")]
    [InlineData("[0,100")]
    [InlineData("{}")]
    [InlineData("\"[0,100,200]\"")]
    [InlineData("[\"0\",\"100\",\"200\"]")]
    [InlineData("[0,1.5,3]")]
    [InlineData("[0,1e2,200]")]
    [InlineData("[0,[1],2]")]
    [InlineData("[0,100,99999999999999999999]")]
    [InlineData("[0,100,200] [1]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ReadAsync_Timestamps_ThatAreNotAStrictlyIncreasingArrayOfIntegersFromZero_AreInvalid_AndNothingIsRead(string? timestamps)
    {
        var streams = Enumerable.Range(0, 3).Select(seed => new RecordingStream(TestJpeg.Noise(16, 16, seed))).ToList();

        var result = await Reader().ReadAsync([.. streams.Select(stream => Part(stream))], timestamps, Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
        Assert.All(streams, stream => Assert.Equal(0, stream.Reads));
    }

    [Fact]
    public async Task ReadAsync_AListOfTimestampsLongerThanAnyHonestOne_IsInvalid()
    {
        var jpeg = TestJpeg.Noise(16, 16, 9);
        var padded = "[0,100,200," + new string(' ', 5000) + "300]";

        var result = await Reader().ReadAsync([Part(jpeg), Part(jpeg), Part(jpeg), Part(jpeg)], padded, Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_AFrameWithALongSideOverTheLimit_IsTooLarge()
    {
        byte[] wide = TestJpeg.Noise(40, 8, 10);
        var reader = Reader(new IdentityPresenceOptions { MaxFrameSide = 32 });

        var result = await reader.ReadAsync([Part(TestJpeg.Noise(32, 24, 11)), Part(wide)], "[0,100]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceTooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_AFrameWithTheLongSideAtTheLimit_IsRead()
    {
        var reader = Reader(new IdentityPresenceOptions { MaxFrameSide = 32 });

        var result = await reader.ReadAsync([Part(TestJpeg.Noise(32, 24, 12))], "[0]", Received, CancellationToken.None);

        Assert.True(result.Succeeded);
        result.Sequence!.Dispose();
    }

    [Fact]
    public async Task ReadAsync_AFrameOverThePixelLimitOfTheDecoder_IsTooLarge()
    {
        // 32 x 24 is 768 pixels: the decoder is given 500, and the header says so before any pixel is decoded.
        var result = await Reader(maxPixels: 500).ReadAsync([Part(TestJpeg.Noise(32, 24, 13))], "[0]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceTooLarge, result.ErrorCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ReadAsync_APartThatIsNotAsLongAsItWasDeclared_IsInvalid(int difference)
    {
        var jpeg = TestJpeg.Noise(16, 16, 14);
        var stream = new RecordingStream(jpeg);
        var declared = jpeg.Length + difference;

        var result = await Reader().ReadAsync([new FramePart(stream, declared, "image/jpeg")], "[0]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
        Assert.All(stream.BuffersOf(declared), buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task ReadAsync_AViolation_StopsAtTheFirstOne_AndZeroesWhatWasRead()
    {
        var first = new RecordingStream(TestJpeg.Noise(16, 16, 15));
        var second = new RecordingStream(TestJpeg.Noise(16, 16, 16));
        var notAJpeg = new RecordingStream(TestImages.Png(8, 8, seed: 2));
        var never = new RecordingStream(TestJpeg.Noise(16, 16, 17));
        FramePart[] parts = [Part(first), Part(second), Part(notAJpeg), Part(never)];

        var result = await Reader().ReadAsync(parts, "[0,100,200,300]", Received, CancellationToken.None);

        Assert.Equal(IdentityApiErrors.PresenceSequenceInvalid, result.ErrorCode);
        Assert.Equal(0, never.Reads);
        foreach (var (stream, part) in new[] { first, second }.Zip(parts))
        {
            var kept = Assert.Single(stream.BuffersOf((int)part.Length));
            Assert.All(kept, value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public async Task ReadAsync_ACancellation_Stops_AndZeroesWhatWasRead()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new RecordingStream(TestJpeg.Noise(16, 16, 18), onEnd: cancellation.Cancel);
        var second = new RecordingStream(TestJpeg.Noise(16, 16, 19));
        FramePart[] parts = [Part(first), Part(second)];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(parts, "[0,100]", Received, cancellation.Token));

        Assert.Equal(0, second.Reads);
        var kept = Assert.Single(first.BuffersOf((int)parts[0].Length));
        Assert.All(kept, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReadAsync_ACancelledToken_ReadsNothing()
    {
        var stream = new RecordingStream(TestJpeg.Noise(16, 16, 20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader().ReadAsync([Part(stream)], "[0]", Received, new CancellationToken(canceled: true)));

        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task FrameSequence_Dispose_ZeroesEveryBuffer()
    {
        // Privacy (ADR-013-R2, R17): the frames never outlive the request. The test holds the very arrays the application held, to look inside them after Dispose.
        var result = await Reader().ReadAsync([Part(TestJpeg.Noise(32, 24, 21)), Part(TestJpeg.Noise(32, 24, 22))], "[0,100]", Received, CancellationToken.None);
        var sequence = result.Sequence!;
        var jpegs = sequence.Frames.Select(frame => frame.Jpeg).ToList();
        var pixels = sequence.Frames.Select(frame => frame.Image.Rgb).ToList();
        Assert.All(jpegs, jpeg => Assert.Contains(jpeg, value => value != 0));
        Assert.All(pixels, rgb => Assert.Contains(rgb, value => value != 0));

        sequence.Dispose();
        sequence.Dispose();

        Assert.All(jpegs, jpeg => Assert.All(jpeg, value => Assert.Equal(0, value)));
        Assert.All(pixels, rgb => Assert.All(rgb, value => Assert.Equal(0, value)));
        Assert.All(sequence.Frames, frame => Assert.Empty(frame.Image.Rgb));
    }

    [Fact]
    public async Task ReadAsync_ALogLine_NamesTheCodeAndTheCountOnly()
    {
        var logger = new CapturingLogger<FrameSequenceReader>();
        var jpeg = TestJpeg.Noise(16, 16, 23);

        await Reader(logger: logger).ReadAsync([Part(jpeg), Part(TestImages.Png(8, 8, seed: 3))], "[0,100]", Received, CancellationToken.None);
        await Reader(new IdentityPresenceOptions { MaxFrames = 1 }, logger: logger).ReadAsync([Part(jpeg), Part(jpeg)], "[0,100]", Received, CancellationToken.None);

        Assert.Equal(
            [
                "A presence sequence was refused: presence_sequence_invalid (2 frames)",
                "A presence sequence was refused: presence_sequence_too_large (2 frames)",
            ],
            logger.Entries.Select(entry => entry.Message));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    private static FrameSequenceReader Reader(IdentityPresenceOptions? presence = null, int maxPixels = 25_000_000, CapturingLogger<FrameSequenceReader>? logger = null)
    {
        var identity = new IdentityVerificationOptions { Presence = presence ?? new IdentityPresenceOptions() };
        return new FrameSequenceReader(
            Options.Create(identity),
            Options.Create(new BiometricModelOptions { MaxImagePixels = maxPixels }),
            logger ?? new CapturingLogger<FrameSequenceReader>());
    }

    private static FramePart Part(byte[] jpeg) => new(new MemoryStream(jpeg), jpeg.Length, "image/jpeg");

    private static FramePart Part(RecordingStream stream) => new(stream, stream.Size, "image/jpeg");

    /// <summary>A stream that counts its reads and keeps the arrays the reader asked it to fill, to look inside them afterwards.</summary>
    private sealed class RecordingStream(byte[] content, Action? onEnd = null) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public int Size { get; } = content.Length;

        public int Reads { get; private set; }

        private List<byte[]> Buffers { get; } = [];

        /// <summary>The arrays of exactly <paramref name="length"/> bytes that the reader filled (not the one-byte probe).</summary>
        public IReadOnlyList<byte[]> BuffersOf(int length) => [.. Buffers.Where(buffer => buffer.Length == length)];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment) && segment.Array is { } array && !Buffers.Contains(array))
                Buffers.Add(array);

            var read = await _inner.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                onEnd?.Invoke();

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
