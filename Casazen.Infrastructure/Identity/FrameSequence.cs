using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// One frame of a presence sequence as the server received it (E72/F01/US01/T02, NF-192, ADR-013-R17): its place in the sequence, the time the browser stamped on
/// it, the picture decoded in memory and the JPEG bytes that came. The bytes stay only because the evidence of the reviewer (E73) may keep the frames the server
/// chose, encrypted; both they and the pixels are zeroed by <see cref="FrameSequence.Dispose"/>. Nothing of a frame goes to a log, a job, an exception or a column.
/// </summary>
/// <param name="Index">The place of the frame in the sequence, from 0, in the order the parts arrived.</param>
/// <param name="TimestampMs">The time of the frame in milliseconds from the first frame of the recording (0 for the first), as the browser stamped it.</param>
/// <param name="Image">The decoded picture, 8 bits per channel, upright; its longest side is at most <c>IdentityVerification:Presence:MaxFrameSide</c>.</param>
/// <param name="Jpeg">The JPEG bytes exactly as they were received, at most <c>IdentityVerification:Presence:MaxFrameBytes</c>.</param>
public sealed record SequenceFrame(int Index, long TimestampMs, RasterImage Image, byte[] Jpeg);

/// <summary>
/// The frames of one presence sequence, in memory only (E72/F01/US01/T02, NF-192, ADR-013-R2, R17): what <see cref="FrameSequenceReader"/> reads from the parts of the
/// request and what the validation of the presence works on. It is the caller's to dispose, in a <c>finally</c> or a <c>using</c>: <see cref="Dispose"/> zeroes every
/// JPEG buffer and every decoded picture, so no frame outlives the request. Disposing twice is safe.
/// </summary>
public sealed class FrameSequence : IDisposable
{
    private readonly SequenceFrame[] _frames;
    private int _disposed;

    /// <summary>A sequence over <paramref name="frames"/> (taken as they are: it owns their buffers from now on), received at <paramref name="receivedAt"/>.</summary>
    public FrameSequence(IEnumerable<SequenceFrame> frames, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = [.. frames];
        ReceivedAt = receivedAt;
    }

    /// <summary>The frames, in the order they were sent. Their buffers are empty or zero after <see cref="Dispose"/>.</summary>
    public IReadOnlyList<SequenceFrame> Frames => _frames;

    /// <summary>The time from the first frame to the last, in milliseconds (0 for a sequence of fewer than two frames).</summary>
    public long DurationMs => _frames.Length < 2 ? 0 : _frames[^1].TimestampMs - _frames[0].TimestampMs;

    /// <summary>When the server received the sequence: what the time since the challenge was issued is measured to.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>Zeroes the JPEG bytes and the pixels of every frame (ADR-013-R2). Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var frame in _frames)
        {
            ImageBytes.Zero(frame.Jpeg);
            frame.Image.Dispose();
        }
    }
}
