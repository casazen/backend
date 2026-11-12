namespace Casazen.Core.Entities.Enums;

/// <summary>The life of a fiscal document (E06/F01/US02/T01). An imported document is always <see cref="Issued"/>.</summary>
public enum FiscalDocumentStatus
{
    /// <summary>Prepared, without a number; it can be refreshed or discarded.</summary>
    Draft = 1,

    /// <summary>Numbered and immutable: its amounts, recipient and lines never change again.</summary>
    Issued = 2,

    /// <summary>A draft that was dropped; it consumed no number.</summary>
    Discarded = 3,
}
