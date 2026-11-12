namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a stored fiscal document (E06/F01/US02/T01). Not the kind of document the rule of a regime gives
/// (<c>Casazen.Core.Options.FiscalDocumentKind</c>): a file that reads both names one with an alias.
/// </summary>
public enum FiscalDocumentKind
{
    /// <summary>An invoice (FatturaPA).</summary>
    Invoice = 1,

    /// <summary>A receipt: not an electronic invoice.</summary>
    Receipt = 2,

    /// <summary>A credit note: it credits all or part of an issued document (<c>CreditedDocumentId</c>).</summary>
    CreditNote = 3,

    /// <summary>A proforma: not a fiscal document (imported from Octorate only).</summary>
    Proforma = 4,
}
