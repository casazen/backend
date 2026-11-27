using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StbImageSharp;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// One part of the request that carries a frame (E72/F01/US01/T02): the stream of its content, the length the client declared for it and the content type it
/// declared. It names no ASP.NET type, so Infrastructure stays free of the web layer: the controller adapts its <c>IFormFileCollection</c> to it. The declared type is
/// not trusted and decides nothing: the bytes do (<see cref="FrameSequenceReader"/>).
/// </summary>
/// <param name="Content">The content of the part, read once from its current position.</param>
/// <param name="Length">The length in bytes the client declared (<c>IFormFile.Length</c>); the reader holds the content to it.</param>
/// <param name="ContentType">The content type the client declared; carried, never used to decide.</param>
public sealed record FramePart(Stream Content, long Length, string? ContentType);

/// <summary>
/// What <see cref="FrameSequenceReader.ReadAsync"/> found: the sequence, which the caller disposes, or the code of the first violation of a limit
/// (<see cref="IdentityApiErrors.PresenceSequenceInvalid"/> or <see cref="IdentityApiErrors.PresenceSequenceTooLarge"/>), with nothing left in memory.
/// </summary>
public sealed class FrameSequenceReadResult
{
    private FrameSequenceReadResult(FrameSequence? sequence, string? errorCode)
    {
        Sequence = sequence;
        ErrorCode = errorCode;
    }

    /// <summary>The frames, read and decoded; null when a limit was violated. The caller disposes it (<see cref="FrameSequence.Dispose"/> zeroes every buffer).</summary>
    public FrameSequence? Sequence { get; }

    /// <summary>The code of the first violation, an <see cref="IdentityApiErrors"/> code the page translates; null when the sequence was read.</summary>
    public string? ErrorCode { get; }

    /// <summary>True when <see cref="Sequence"/> is there.</summary>
    [MemberNotNullWhen(true, nameof(Sequence))]
    [MemberNotNullWhen(false, nameof(ErrorCode))]
    public bool Succeeded => Sequence is not null;

    internal static FrameSequenceReadResult Success(FrameSequence sequence)
    {
        return new FrameSequenceReadResult(sequence, null);
    }

    internal static FrameSequenceReadResult Failure(string errorCode)
    {
        return new FrameSequenceReadResult(null, errorCode);
    }
}

/// <summary>
/// The intake of the frames of a presence sequence (E72/F01/US01/T02, NF-192, ADR-013-R2, R17, R22, decision D6): the browser sends a dense sequence of timestamped
/// JPEG frames, not a video, so the server decodes no video, runs no ffmpeg and has no new native dependency: each part is a JPEG that the managed decoder of the
/// check-in (<see cref="RasterImage"/>, StbImageSharp) reads in memory. Nothing is written to disk.
/// <para>
/// <b>Limits</b> (the section <c>IdentityVerification:Presence</c>, and <c>Biometrics:MaxImagePixels</c> for the decoder), each with its error code: a sequence of no
/// frame, a part that is not a JPEG whatever its declared type (the bytes begin <c>FF D8 FF</c>), a part whose stream is not as long as it was declared, and
/// timestamps that are not a JSON array of as many non-negative integers as the frames, strictly increasing and beginning with 0, or a frame that cannot be decoded,
/// are <see cref="IdentityApiErrors.PresenceSequenceInvalid"/>; more frames than <c>MaxFrames</c>, a part over <c>MaxFrameBytes</c>, all the parts over
/// <c>MaxSequenceBytes</c>, and a frame whose longest side is over <c>MaxFrameSide</c> or whose pixels are over the decoder limit are
/// <see cref="IdentityApiErrors.PresenceSequenceTooLarge"/>. What the declared lengths can already say is refused before a byte is read, so the buffers are never
/// larger than the limits.
/// </para>
/// <para>
/// <b>Memory</b>: the parts are read one after the other, each into an array of exactly its declared length (the pool is not used for it: the JPEG stays until the
/// sequence is disposed and the evidence of E73 may encrypt the frames the server chooses, which wants an exact array). The first violation stops the intake and
/// zeroes what was read; so does a cancellation or an exception. Only the code and the counts reach the log, never a byte, a size of a person or a timestamp.
/// </para>
/// <para>The reader does not judge presence: whether the frames are a person who answers the challenge is <see cref="PresenceSequenceValidator"/>'s.</para>
/// </summary>
public sealed class FrameSequenceReader(
    IOptions<IdentityVerificationOptions> identityOptions,
    IOptions<BiometricModelOptions> biometricOptions,
    ILogger<FrameSequenceReader> logger)
{
    /// <summary>The characters one timestamp may take in the JSON: a 64-bit integer has at most 19 digits, and the comma and a space come with it.</summary>
    private const int MaxTimestampCharacters = 24;

    /// <summary>The depth of the JSON of the timestamps: the array and nothing inside it.</summary>
    private const int TimestampsMaxDepth = 2;

    /// <summary>
    /// Reads the frames of <paramref name="parts"/> with the timestamps of <paramref name="timestampsJson"/>, stamped as received at <paramref name="receivedAt"/>.
    /// The first violation is the answer; a cancellation is an <see cref="OperationCanceledException"/>, after what was read has been zeroed.
    /// </summary>
    public async Task<FrameSequenceReadResult> ReadAsync(
        IReadOnlyList<FramePart> parts,
        string? timestampsJson,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var limits = identityOptions.Value.Presence;
        var maxPixels = biometricOptions.Value.MaxImagePixels;

        // What the declared sizes and the timestamps say is settled before the first byte is read.
        var refusal = CheckDeclared(parts, limits);
        if (refusal is not null)
            return Refuse(refusal, parts.Count);

        var timestamps = ParseTimestamps(timestampsJson, parts.Count);
        if (timestamps is null)
            return Refuse(IdentityApiErrors.PresenceSequenceInvalid, parts.Count);

        var frames = new List<SequenceFrame>(parts.Count);
        var handedOver = false;
        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var (frame, error) = await ReadFrameAsync(parts[index], index, timestamps[index], limits, maxPixels, ct);
                if (frame is null)
                    return Refuse(error ?? IdentityApiErrors.PresenceSequenceInvalid, parts.Count);

                frames.Add(frame);
            }

            handedOver = true;
            return FrameSequenceReadResult.Success(new FrameSequence(frames, receivedAt));
        }
        finally
        {
            // A violation, a cancellation or a failure of the stream: nothing that was read stays in memory.
            if (!handedOver)
            {
                foreach (var frame in frames)
                {
                    ImageBytes.Zero(frame.Jpeg);
                    frame.Image.Dispose();
                }
            }
        }
    }

    /// <summary>The first limit that the declared sizes break, or null: the count of the frames, the length of each and the length of all.</summary>
    private static string? CheckDeclared(IReadOnlyList<FramePart> parts, IdentityPresenceOptions limits)
    {
        if (parts.Count == 0)
            return IdentityApiErrors.PresenceSequenceInvalid;

        if (parts.Count > limits.MaxFrames)
            return IdentityApiErrors.PresenceSequenceTooLarge;

        long total = 0;
        foreach (var part in parts)
        {
            if (part?.Content is null || part.Length <= 0)
                return IdentityApiErrors.PresenceSequenceInvalid;

            if (part.Length > limits.MaxFrameBytes)
                return IdentityApiErrors.PresenceSequenceTooLarge;

            total += part.Length;
            if (total > limits.MaxSequenceBytes)
                return IdentityApiErrors.PresenceSequenceTooLarge;
        }

        return null;
    }

    /// <summary>
    /// The timestamps, or null when they are not a JSON array of exactly <paramref name="frames"/> non-negative integers, strictly increasing, the first being 0 (a
    /// fraction, an exponent, a string, a negative value, a repeated or a lower one, a count that differs from the frames, and anything that is not strict JSON).
    /// </summary>
    private static long[]? ParseTimestamps(string? json, int frames)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > ((long)frames * MaxTimestampCharacters) + MaxTimestampCharacters)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = TimestampsMaxDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != frames)
                return null;

            var values = new long[frames];
            var next = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var value) || value < 0)
                    return null;

                // The first value is 0 and each one is above the one before it.
                if (next == 0 ? value != 0 : value <= values[next - 1])
                    return null;

                values[next++] = value;
            }

            return values;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One frame: the bytes (exactly the declared length), their magic, the size from the header before any pixel is decoded, then the picture. The frame, or the
    /// code of what is wrong with it; whatever was read for a frame that is refused is zeroed here.
    /// </summary>
    private static async Task<(SequenceFrame? Frame, string? Error)> ReadFrameAsync(
        FramePart part,
        int index,
        long timestampMs,
        IdentityPresenceOptions limits,
        int maxPixels,
        CancellationToken ct)
    {
        var bytes = new byte[checked((int)part.Length)];
        RasterImage? image = null;
        var handedOver = false;
        try
        {
            if (!await FillAsync(part.Content, bytes, ct))
                return (null, IdentityApiErrors.PresenceSequenceInvalid);

            // The declared type is not trusted: a JPEG begins FF D8 FF.
            if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
                return (null, IdentityApiErrors.PresenceSequenceInvalid);

            if (ReadSize(bytes) is not { } size)
                return (null, IdentityApiErrors.PresenceSequenceInvalid);

            // A small file can hold a huge picture: the header says so before it is decoded.
            if (Math.Max(size.Width, size.Height) > limits.MaxFrameSide || (long)size.Width * size.Height > maxPixels)
                return (null, IdentityApiErrors.PresenceSequenceTooLarge);

            try
            {
                image = RasterImage.Decode(bytes, maxPixels);
            }
            catch (InvalidDataException)
            {
                return (null, IdentityApiErrors.PresenceSequenceInvalid);
            }

            if (Math.Max(image.Width, image.Height) > limits.MaxFrameSide)
                return (null, IdentityApiErrors.PresenceSequenceTooLarge);

            handedOver = true;
            return (new SequenceFrame(index, timestampMs, image, bytes), null);
        }
        finally
        {
            if (!handedOver)
            {
                ImageBytes.Zero(bytes);
                image?.Dispose();
            }
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from the stream: false when the stream is shorter than declared or has more after it.</summary>
    private static async Task<bool> FillAsync(Stream content, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await content.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        // The length a client declares is not the length it sends: a byte more is the proof that the part is longer than it said.
        var probe = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            return await content.ReadAsync(probe.AsMemory(0, 1), ct) == 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe, clearArray: true);
        }
    }

    /// <summary>The size in the header of the picture, without decoding it; null when it has none that the decoder understands.</summary>
    private static (int Width, int Height)? ReadSize(byte[] bytes)
    {
        try
        {
            using var probe = new MemoryStream(bytes, writable: false);
            var info = ImageInfo.FromStream(probe);
            return info is { Width: > 0, Height: > 0 } found ? (found.Width, found.Height) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    private FrameSequenceReadResult Refuse(string code, int frames)
    {
        // The code and the count only: never a byte, a timestamp or a size that could say who is in front of the camera.
        logger.LogInformation("A presence sequence was refused: {Code} ({Frames} frames)", code, frames);
        return FrameSequenceReadResult.Failure(code);
    }
}
