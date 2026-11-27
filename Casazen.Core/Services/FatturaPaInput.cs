namespace Casazen.Core.Services;

/// <summary>
/// Everything a FatturaPA document is written from (FIS-03, NF-83, NF-116, E06/F01/US02/T03, E17/F01/US01/T03), neutral: the callers build it from a stored
/// <c>FiscalDocument</c>, its lines and the snapshot of its issuer. Owner statements are outside it: no agency-to-owner XML is ever generated from a statement.
/// </summary>
/// <param name="Transmitter">Who transmits (<c>IdTrasmittente</c>): the fiscal code of the subject that sends the file through its channel: the agency for a document it
/// issues for an owner (or in its own name), until a provider of the SDI is configured (E17/F01/US02).</param>
/// <param name="ProgressivoInvio">The progressive of the transmission (at most 10 letters or digits).</param>
/// <param name="Issuer">The issuer (<c>CedentePrestatore</c>).</param>
/// <param name="Recipient">The recipient (<c>CessionarioCommittente</c>).</param>
/// <param name="RecipientCode">The SDI code of the recipient (7 characters); <c>null</c> for none.</param>
/// <param name="RecipientPec">The PEC of the recipient, used with the code <c>0000000</c>.</param>
/// <param name="DocumentTypeCode">The type of document of the rule (<c>TD01</c>, <c>TD04</c>, <c>TD17</c>).</param>
/// <param name="Number">The display number (at most 20 characters).</param>
/// <param name="IssueDate">The day of the issue (a Europe/Rome calendar day).</param>
/// <param name="Currency">The ISO 4217 code of every amount.</param>
/// <param name="Lines">The lines, net of VAT, as <see cref="FiscalDocumentTotals"/> computed them.</param>
/// <param name="Summary">One row per VAT rate and nature code, as <see cref="FiscalDocumentTotals"/> computed them.</param>
/// <param name="StampDuty">The stamp duty of the document (zero when not due).</param>
/// <param name="Total">The total of the document, as stored.</param>
/// <param name="RelatedDocument">For a credit note, the document it credits (for a self-billed TD17, the invoice of the foreign supplier).</param>
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
    FatturaPaRelatedDocument? RelatedDocument)
{
    /// <summary>
    /// Who issues the document when it is not the cedente itself (<c>SoggettoEmittente</c>): <see cref="FatturaPaIssuedBy.ThirdParty"/> (<c>TZ</c>) for the agency that
    /// issues on behalf of the owner (E17/F01/US01/T03), with <see cref="ThirdPartyIssuer"/>; <see cref="FatturaPaIssuedBy.Recipient"/> (<c>CC</c>) for a document the
    /// recipient issues itself, a self-billed TD17 (E17/F01/US03). The default, <see cref="FatturaPaIssuedBy.Issuer"/>, writes no element.
    /// </summary>
    public FatturaPaIssuedBy IssuedBy { get; init; } = FatturaPaIssuedBy.Issuer;

    /// <summary>The third party that issues on behalf of the cedente (<c>TerzoIntermediarioOSoggettoEmittente</c>): required with <see cref="FatturaPaIssuedBy.ThirdParty"/>.</summary>
    public FatturaPaParty? ThirdPartyIssuer { get; init; }
}

/// <summary>Who issues a FatturaPA document (<c>SoggettoEmittente</c>).</summary>
public enum FatturaPaIssuedBy
{
    /// <summary>The cedente itself: no <c>SoggettoEmittente</c>.</summary>
    Issuer = 0,

    /// <summary><c>TZ</c>: a third party issues on behalf of the cedente (the agency for its owner).</summary>
    ThirdParty = 1,

    /// <summary><c>CC</c>: the recipient issues the document (a self-billed invoice, TD17).</summary>
    Recipient = 2,
}

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

/// <summary>The fixed rules of the parties of FatturaPA (Specifiche tecniche FatturaPA 1.4, the schema 1.2.3).</summary>
public static class FatturaPaParties
{
    /// <summary>The <c>IdCodice</c> of a recipient abroad who has no VAT number (a private person): eleven nines, with the country as <c>IdPaese</c>.</summary>
    public const string ForeignPrivateCode = "99999999999";

    /// <summary>The <c>CAP</c> of an address abroad: the schema wants five digits, the specifications five zeros.</summary>
    public const string ForeignPostalCode = "00000";
}
