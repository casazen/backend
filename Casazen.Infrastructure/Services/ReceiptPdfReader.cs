namespace Casazen.Infrastructure.Services;

internal static class ReceiptPdfReader
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    /// <summary>
    /// The whole content when it is a PDF (<c>%PDF-</c> signature) of at most <paramref name="maxBytes"/>, positioned at the start; null otherwise.
    /// The declared length is not trusted: the content is counted while it is read.
    /// </summary>
    public static async Task<MemoryStream?> ReadPdfAsync(Stream content, long? declaredLength, long maxBytes, CancellationToken cancellationToken)
    {
        if (declaredLength <= 0 || declaredLength > maxBytes)
            return null;

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                await buffer.DisposeAsync();
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length < PdfSignature.Length || !buffer.GetBuffer().AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature))
        {
            await buffer.DisposeAsync();
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }
}
