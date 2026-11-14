namespace Casazen.Core.Options;

/// <summary>
/// The switches of the fiscal documents (FIS-02, NF-82, E06/F02/US02/T02, E11/F01/US01), bound from the section <c>FiscalDocuments</c> of
/// <c>appsettings.json</c>. The tax rules themselves (the kind of document, the VAT of every line type, the nature codes, the stamp duty) are the
/// effective-dated catalog <c>FiscalDocuments:TaxRules</c> (<see cref="FiscalTaxRulesOptions"/>, E11/F01/US01/T02), which replaced the rules per regime.
/// </summary>
public class FiscalDocumentRulesOptions
{
    public const string SectionName = "FiscalDocuments";

    /// <summary>
    /// The answer to D34: Octorate can issue every booking. Kept for the clients that read it; CasaZen is the only issuer since 2026-11-14 and nothing
    /// recommends Octorate any more. Off by default.
    /// </summary>
    public bool OctorateIssuesAllBookings { get; set; }

    /// <summary>
    /// Whether the job <c>octorate-document-import</c> reads the documents Octorate issued (E11/F01/US01/T01, D1). Off by default: CasaZen is the only
    /// issuer since 2026-11-14 and the import is kept only to read old documents, so it never runs by accident. While it is off the job selects no
    /// organization and an import reads nothing; on, it reads the organizations connected to Octorate in this environment. An imported document never
    /// locks the issuer of a booking, but it is a document issued elsewhere: CasaZen issues no second document for that booking (409).
    /// </summary>
    public bool ImportOctorateDocuments { get; set; }
}

/// <summary>What kind of document the issuer of a regime gives (<see cref="FiscalTaxRuleSetOptions.DocumentKind"/>).</summary>
public enum FiscalDocumentKind
{
    /// <summary>Not decided: a rule set of this kind stops the startup (the catalog accepts only a receipt or an invoice).</summary>
    None = 0,

    /// <summary>A receipt: no VAT (an Italian private person, a Swiss issuer that is not registered).</summary>
    Receipt = 1,

    /// <summary>An invoice (FatturaPA in Italy, the document of MWSTG art. 26 in Switzerland).</summary>
    Invoice = 2,

    /// <summary>An invoice with a double heading, owner and agency. CasaZen does not issue it today; the member is the one of the configuration format.</summary>
    DualHeadingInvoice = 3,
}
