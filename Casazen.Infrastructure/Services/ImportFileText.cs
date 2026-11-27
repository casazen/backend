using System.Text;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// How the services that import an official list from a file (<see cref="AlloggiatiCodeTableService"/>, <see cref="ComuneImportService"/>) read it: the
/// bytes, within a size limit, and the text in them.
/// </summary>
internal static class ImportFileText
{
    /// <summary>UTF-8 (with or without BOM) when valid, otherwise Windows-1252; null when neither applies.</summary>
    public static string? Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(
                    1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return windows1252?.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
    }

    /// <summary>The whole content, or <c>null</c> when it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAllAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
