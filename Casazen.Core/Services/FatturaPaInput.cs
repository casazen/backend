namespace Casazen.Core.Services;

/// <summary>
/// Everything a FatturaPA document is written from (FIS-03, NF-83, E06/F01/US02/T03), neutral: the callers build it from a stored
/// <c>FiscalDocument</c>, its lines and the profile of the issuer. Owner statements are outside it: no agency-to-owner XML is ever generated.
/// </summary>
/// <param name="Transmitter">Who transmits: the issuer's own fiscal id when no intermediary is configured.</param>
/// <param name="ProgressivoInvio">The progressive of the transmission (at most 10 letters or digits).</param>
/// <param name="Issuer">The issuer (<c>CedentePrestatore</c>).</param>
/// <param name="Recipient">The recipient (<c>CessionarioCommittente</c>).</param>
/// <param name="RecipientCode">The SDI code of the recipient (7 characters); <c>null</c> for none.</param>
/// <param name="RecipientPec">The PEC of the recipient, used with the code <c>0000000</c>.</param>
/// <param name="DocumentTypeCode">The type of document of the rule (<c>TD01</c>, <c>TD04</c>).</param>
/// <param name="Number">The display number (at most 20 characters).</param>
/// <param name="IssueDate">The day of the issue (a Europe/Rome calendar day).</param>
/// <param name="Currency">The ISO 4217 code of every amount.</param>
/// <param name="Lines">The lines, net of VAT, as <see cref="FiscalDocumentTotals"/> computed them.</param>
/// <param name="Summary">One row per VAT rate and nature code, as <see cref="FiscalDocumentTotals"/> computed them.</param>
/// <param name="StampDuty">The stamp duty of the document (zero when not due).</param>
/// <param name="Total">The total of the document, as stored.</param>
/// <param name="RelatedDocument">For a credit note, the document it credits.</param>
public sealed record FatturaPaInput(
    FatturaPaId Transmitter,
    string ProgressivoInvio,
    FatturaPaParty Issuer,
    FatturaPaParty Recipient,
    string? RecipientCode,
    string? RecipientPec,
    string DocumentTypeCode,
    string Number,
    DateOnly IssueDate,
    string Currency,
    IReadOnlyList<FiscalDocumentLineAmounts> Lines,
    IReadOnlyList<FiscalDocumentVatSummary> Summary,
    decimal StampDuty,
    decimal Total,
    FatturaPaRelatedDocument? RelatedDocument);

/// <summary>A fiscal id of FatturaPA: the country and the code.</summary>
public sealed record FatturaPaId(string CountryCode, string Code);

/// <summary>
/// A party of the document. <paramref name="Denomination"/> for a company, else <paramref name="FirstName"/> and <paramref name="LastName"/>; the VAT
/// id (<paramref name="VatId"/>) and/or the fiscal code; the regime code for the issuer only.
/// </summary>
public sealed record FatturaPaParty(
    FatturaPaId? VatId,
    string? FiscalCode,
    string? Denomination,
    string? FirstName,
    string? LastName,
    FatturaPaAddress Address,
    string? RegimeCode = null);

/// <summary>A structured address (<c>Sede</c>).</summary>
public sealed record FatturaPaAddress(string Street, string? Civic, string PostalCode, string City, string? Province, string CountryCode);

/// <summary>The document a credit note credits (<c>DatiFattureCollegate</c>).</summary>
public sealed record FatturaPaRelatedDocument(string Number, DateOnly? IssueDate);
