namespace Casazen.Core.Entities.Enums;

/// <summary>The life of a fiscal document (E06/F01/US02/T01). An imported document is always <see cref="Issued"/>.</summary>
public enum FiscalDocumentStatus
{
    /// <summary>Prepared, without a number; it can be refreshed or discarded.</summary>
    Draft = 1,

    /// <summary>Numbered and immutable: its amounts, recipient and lines never change again.</summary>
    Issued = 2,

    /// <summary>
    /// A draft that was dropped; it consumed no number. Two documents that were numbered end here too, and keep what they hold for the history (FIX/BE-TD17-JOB-EXIT): the TD17 the SDI rejected that a correction
    /// replaced (its display number, never its sequence number), and the replacement draft of such a TD17 that was closed as «non più dovuta» (<c>FiscalDocument.ClosedNotDueAt</c>), which keeps its sequence
    /// number: that number is never given again.
    /// </summary>
    Discarded = 3,
}
