using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Options;

/// <summary>
/// What the fiscal documents of CasaZen are made of (FIS-02, NF-82, PO-05, E06/F02/US02/T02), bound from the section <c>FiscalDocuments</c> of
/// <c>appsettings.json</c>: the rule of each regime, the stamp duty and the answer to D34. The rules live in the configuration and not in the code, so
/// the accountant's decision changes a variable and not the application.
/// </summary>
/// <remarks>
/// <b>No live fallback.</b> <c>appsettings.json</c> ships the structure of the rules with the codes of the electronic invoice that are facts of the
/// format (<c>TD01</c>, <c>TD04</c>, <c>RF01</c>, <c>RF19</c>) and nothing the accountant has not approved: the kind of document, the VAT rate, the
/// nature code of an exempt operation and the nature code of the tourist tax stay empty. A rule that is not complete is
/// <see cref="FiscalDocumentRule.IsConfigured"/> false and issuance refuses with <c>document_rule_not_configured</c> instead of guessing a rate.
/// Nothing here stops the application from starting: a bad value only makes its rule unusable.
/// </remarks>
public class FiscalDocumentRulesOptions
{
    public const string SectionName = "FiscalDocuments";

    /// <summary>
    /// The answer to D34: Octorate can issue every booking, the booking engine of the agency's site and the direct bookings CasaZen creates
    /// included. Off by default. Only while it is on is Octorate the issuer the wizard recommends.
    /// </summary>
    public bool OctorateIssuesAllBookings { get; set; }

    /// <summary>
    /// The taxable amount above which the stamp duty is due on a document without VAT (<c>AgencyInvoiceService</c> holds the same figure as a
    /// constant today; E06/F01/US02/T05 makes it read this one).
    /// </summary>
    public decimal StampDutyThreshold { get; set; } = 77.47m;

    /// <summary>The stamp duty due above <see cref="StampDutyThreshold"/>, in euros.</summary>
    public decimal StampDutyAmount { get; set; } = 2.00m;

    /// <summary>
    /// Whether the stamp duty of a document is charged to the guest and so added to its total (E06/F01/US02/T03). Off by default: the issuer bears it
    /// and the total is what the guest paid for the stay; only the accountant's decision turns it on.
    /// </summary>
    public bool StampDutyChargedToRecipient { get; set; }

    /// <summary>The rule of each regime of the issuer.</summary>
    public Dictionary<DocumentRegime, FiscalDocumentRule> Rules { get; set; } = new();

    /// <summary>The rule of <paramref name="regime"/>, or <c>null</c> when the configuration has none.</summary>
    public FiscalDocumentRule? RuleOf(DocumentRegime regime) => Rules.TryGetValue(regime, out var rule) ? rule : null;
}

/// <summary>What kind of document the issuer of a regime gives to the guest.</summary>
public enum FiscalDocumentKind
{
    /// <summary>Not decided: nothing is issued and issuance refuses with <c>document_rule_not_configured</c>. The shipped default.</summary>
    None = 0,

    /// <summary>A receipt: a PDF that is not a fiscal document, with no VAT.</summary>
    Receipt = 1,

    /// <summary>An invoice (FatturaPA).</summary>
    Invoice = 2,

    /// <summary>An invoice with a double heading, owner and agency. CasaZen does not issue it today; the member is the one of the configuration format.</summary>
    DualHeadingInvoice = 3,
}

/// <summary>
/// The rule of one regime (<see cref="FiscalDocumentRulesOptions.Rules"/>): which document, with which VAT, which codes of the electronic invoice,
/// and the pattern of the series.
/// </summary>
public class FiscalDocumentRule
{
    /// <summary>The kind of document; <see cref="FiscalDocumentKind.None"/> until the accountant approves it.</summary>
    public FiscalDocumentKind DocumentKind { get; set; } = FiscalDocumentKind.None;

    /// <summary>The regime code of FatturaPA for this regime (<c>RF01</c> ordinary, <c>RF19</c> flat-rate); empty for a private person. The issuer's profile can override it.</summary>
    public string? FatturaPaRegimeCode { get; set; }

    /// <summary>The VAT rate of the document in percent; <c>null</c> until the accountant approves it. Never defaulted.</summary>
    public decimal? VatRatePercent { get; set; }

    /// <summary>The nature code of an operation without VAT (<c>N2.2</c>...), used when <see cref="VatRatePercent"/> is 0.</summary>
    public string? VatNatureCode { get; set; }

    /// <summary>The type of document of an invoice in FatturaPA.</summary>
    public string InvoiceTypeCode { get; set; } = "TD01";

    /// <summary>The type of document of a credit note in FatturaPA.</summary>
    public string CreditNoteTypeCode { get; set; } = "TD04";

    /// <summary>The nature code of the tourist tax line of the document.</summary>
    public string? TouristTaxNatureCode { get; set; }

    /// <summary>The series the documents are numbered in, with <c>{year}</c> for the year of the document.</summary>
    public string SeriesPattern { get; set; } = "{year}";

    /// <summary>
    /// True when the rule is complete enough to issue with: a document kind has been approved, and an invoice has a VAT rate between 0 and 100
    /// (and the nature code of an operation without VAT when the rate is 0) and the codes of its documents. A receipt needs no VAT. A value out of
    /// range makes the rule unusable, never an error at startup.
    /// </summary>
    public bool IsConfigured =>
        DocumentKind switch
        {
            FiscalDocumentKind.Receipt => true,
            FiscalDocumentKind.Invoice or FiscalDocumentKind.DualHeadingInvoice =>
                VatRatePercent is >= 0m and <= 100m
                && (VatRatePercent > 0m || !string.IsNullOrWhiteSpace(VatNatureCode))
                && !string.IsNullOrWhiteSpace(InvoiceTypeCode)
                && !string.IsNullOrWhiteSpace(CreditNoteTypeCode)
                && !string.IsNullOrWhiteSpace(SeriesPattern),
            _ => false,
        };
}
