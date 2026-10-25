namespace Casazen.Core.Leases.Reading;

/// <summary>
/// The stable codes a document that could not be read carries in <c>LeaseDocument.ExtractionError</c> (NL-DOCS): never a message, never
/// a word of the document. The interface turns each into a sentence and goes on with the wizard by hand.
/// </summary>
public static class ContractReadingCodes
{
    /// <summary>The file is not in the storage any more.</summary>
    public const string FileMissing = "file_missing";

    /// <summary>Not a PDF that can be opened, or too many pages to be a contract.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The pages need OCR and it cannot run now (tools missing, busy, out of time) and nothing could be read without it.</summary>
    public const string OcrUnavailable = "ocr_unavailable";

    /// <summary>OCR ran and found no text: a blank page, a photo of something else, a page too dark.</summary>
    public const string NoText = "no_text";

    /// <summary>There is text but not a contract's data in it (or none that the draft can take).</summary>
    public const string NothingFound = "nothing_found";

    /// <summary>An unexpected failure (the job is tried again).</summary>
    public const string Error = "error";
}
