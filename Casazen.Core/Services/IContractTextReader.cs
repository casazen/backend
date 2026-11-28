namespace Casazen.Core.Services;

public sealed record ContractText(string Text, int Pages, int PagesOcr, bool Truncated)
{
    public IReadOnlyList<string> PageTexts() => Text.Split('\f').Select(page => page.Trim()).ToList();
}

/// <summary>
/// Reads the text of a contract PDF on the server: the text it carries when it is a digital PDF, OCR (Tesseract, Italian and English,
/// installed in the image of the API) for the pages of a scan or of a photo. Nothing leaves the host and no temporary file outlives the
/// call. Contains personal data (the text of a contract): never log it.
/// </summary>
public interface IContractTextReader
{
    Task<ContractText> ReadAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>
    /// The text a digital PDF carries, in process, with no OCR at all (the receipt of the registration of the Agenzia delle Entrate is one):
    /// <c>null</c> when the file holds no text (a scan or a photo) or cannot be opened. It never waits for an OCR slot, so it can answer a request.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are empty or too large to be a document.</exception>
    Task<ContractText?> ReadDigitalAsync(byte[] pdf, CancellationToken cancellationToken = default);
}

public sealed class ContractTextUnavailableException(Exception? inner = null) : Exception("Contract OCR unavailable", inner);
