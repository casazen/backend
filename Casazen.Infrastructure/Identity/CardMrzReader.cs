using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The one place that reads the MRZ of a picture of an identity document, for the flow and for the qualification runner alike. It is read on the card the geometry straightened: on the whole
/// photo the ground around the card (a desk, a table) fills the rows the band is looked for in, and none of the 38 backs of the pilot was read that way. The card comes upright from the geometry,
/// so it is read as it lies and turned the other ways only when no MRZ block is found in it (<see cref="IMrzExtractionService.ReadUprightAsync"/>); a picture with no card to straighten is read
/// as before, in every turn.
/// </summary>
internal static class CardMrzReader
{
    internal static async Task<MrzReading> ReadAsync(
        IMrzExtractionService mrz, DocumentPicture picture, DocumentGeometryReport report, byte[] photo, int maxWorkingSide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mrz);
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(photo);

        var onCard = await picture.ReadOnCardAsync(report, card => ReadCardAsync(mrz, card, maxWorkingSide, cancellationToken));
        return onCard ?? await ReadPhotoAsync(mrz, photo, cancellationToken);
    }

    internal static async Task<MrzReading> ReadCardAsync(IMrzExtractionService mrz, RasterImage card, int maxWorkingSide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mrz);
        ArgumentNullException.ThrowIfNull(card);

        RasterImage? scaled = null;
        byte[]? png = null;
        try
        {
            var source = card;
            var longest = Math.Max(card.Width, card.Height);

            // The PNG of a card cut from a large photo is larger than the reader accepts, and the locator of the band brings the card down to this size anyway.
            if (longest > maxWorkingSide)
            {
                scaled = card.Resized(
                    Math.Max(1, (int)Math.Round((double)card.Width * maxWorkingSide / longest)),
                    Math.Max(1, (int)Math.Round((double)card.Height * maxWorkingSide / longest)));
                source = scaled;
            }

            png = CardPngEncoder.Encode(source);
            using var stream = new MemoryStream(png, writable: false);
            return await mrz.ReadUprightAsync(stream, cancellationToken);
        }
        finally
        {
            ImageBytes.Zero(png);
            scaled?.Dispose();
        }
    }

    internal static async Task<MrzReading> ReadPhotoAsync(IMrzExtractionService mrz, byte[] photo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mrz);
        ArgumentNullException.ThrowIfNull(photo);

        using var stream = new MemoryStream(photo, writable: false);
        return await mrz.ReadAsync(stream, cancellationToken);
    }
}
