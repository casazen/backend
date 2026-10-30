namespace Casazen.Core.Services;

/// <summary>
/// The text of one contract document (NL-DOCS). <see cref="PagesOcr"/> pages had no text of their own and were read by OCR; the others
/// were read from the text the PDF carries. <see cref="Truncated"/> says that some pages that needed OCR were left unread (the limit).
/// The pages are joined by a form feed: <see cref="PageTexts"/> gives them back one by one (the section of the document a page belongs to
/// is told from its own text, CF-DOCS-REAL).
/// </summary>
public sealed record ContractText(string Text, int Pages, int PagesOcr, bool Truncated)
{
    /// <summary>The text of each page, the first page first. A text that was never split in pages (a single block) is one page.</summary>
    public IReadOnlyList<string> PageTexts() => Text.Split('\f').Select(page => page.Trim()).ToList();
}

/// <summary>
/// Reads the text of a contract PDF on the server: the text it carries when it is a digital PDF, OCR (Tesseract, Italian and English,
/// installed in the image of the API) for the pages of a scan or of a photo. Nothing leaves the host and no temporary file outlives the
/// call. Contains personal data (the text of a contract): never log it.
/// </summary>
public interface IContractTextReader
{
    /// <param name="pdf">The bytes of the PDF (a photo is a PDF already: the upload turns it into one).</param>
    /// <param name="cancellationToken">Cancels the read; an OCR that runs out of time is <see cref="ContractTextUnavailableException"/>, not a cancellation.</param>
    /// <exception cref="InvalidDataException">The bytes are not a PDF that can be opened, or have more pages than a contract can.</exception>
    /// <exception cref="ContractTextUnavailableException">Pages need OCR and it cannot run (tools missing, out of time, busy) and nothing could be read without it.</exception>
    Task<ContractText> ReadAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>
    /// The text a digital PDF carries, in process, with no OCR at all (the receipt of the registration of the Agenzia delle Entrate is one):
    /// <c>null</c> when the file holds no text (a scan or a photo) or cannot be opened. It never waits for an OCR slot, so it can answer a request.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are empty or too large to be a document.</exception>
    Task<ContractText?> ReadDigitalAsync(byte[] pdf, CancellationToken cancellationToken = default);
}

/// <summary>The OCR is not available now: the tools are not installed, the server is busy or the read ran out of time.</summary>
public sealed class ContractTextUnavailableException(Exception? inner = null) : Exception("Contract OCR unavailable", inner);
