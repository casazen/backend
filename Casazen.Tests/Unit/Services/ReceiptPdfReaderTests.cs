using System.Text;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The reading of the receipt that the Questura communication and the RLI registration share: a PDF, of a limited size that is counted while the content is read.
/// </summary>
public class ReceiptPdfReaderTests
{
    private const long Limit = 64;

    private static byte[] Pdf(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        Encoding.ASCII.GetBytes("%PDF-").CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public async Task ReadPdfAsync_PdfWithinTheLimit_ReturnsItFromTheStart()
    {
        var bytes = Pdf(40);
        await using var content = new MemoryStream(bytes);

        await using var result = await ReceiptPdfReader.ReadPdfAsync(content, bytes.Length, Limit, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.Position);
        Assert.Equal(bytes, result.ToArray());
    }

    [Fact]
    public async Task ReadPdfAsync_ContentWithoutThePdfSignature_ReturnsNull()
    {
        await using var content = new MemoryStream(Encoding.ASCII.GetBytes("GIF89a this is not a receipt"));

        var result = await ReceiptPdfReader.ReadPdfAsync(content, null, Limit, CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task ReadPdfAsync_DeclaredLengthNotTrusted_CountsWhileReadingAndRefusesOneByteOverTheLimit(int length, bool accepted)
    {
        await using var content = new MemoryStream(Pdf(length));

        await using var result = await ReceiptPdfReader.ReadPdfAsync(content, null, Limit, CancellationToken.None);

        Assert.Equal(accepted, result is not null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Limit + 1)]
    public async Task ReadPdfAsync_DeclaredLengthEmptyOrOverTheLimit_ReturnsNullWithoutReading(long declaredLength)
    {
        await using var content = new MemoryStream(Pdf(40));

        var result = await ReceiptPdfReader.ReadPdfAsync(content, declaredLength, Limit, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, content.Position);
    }
}
