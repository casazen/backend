using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// A fiscal document as the API gives it (E06/F01/US01/T01): stable codes for the kind, the status, the source, the payment and the SDI state (never a
/// display text), the stored amounts in <see cref="Currency"/>, the recipient snapshot and the lines. The XML and the PDF are separate downloads.
/// The answer for one document (FIX/RES-DOCS) also says who collected the money (<see cref="PaymentChannel"/>) and who issued the document as it was when
/// it was issued (<see cref="Issuer"/>), with the type and the VAT of every line: the names are the ones the drawer of the frontend reads, and everything is
/// data the document already holds (nothing is looked up for it).
/// </summary>
public sealed record FiscalDocumentDto
{
    public Guid Id { get; init; }
    public Guid? BookingId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// What the document is for: <c>GuestStay</c>, <c>OwnerInvoice</c> or <c>SelfBilling</c>. A self-billed invoice (TD17) keeps the foreign supplier in the recipient fields and the
    /// holder that issues it is the issuer (see <c>api/fiscal/self-billing</c>), so a reader that takes the recipient for a customer has to tell it by this field.
    /// </summary>
    public string Purpose { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Series { get; init; }
    public int? SequenceNumber { get; init; }
    public string? Number { get; init; }
    public DateOnly? IssueDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? RecipientKind { get; init; }
    public string? RecipientName { get; init; }
    public string? RecipientFiscalCode { get; init; }
    public string? RecipientVatNumber { get; init; }
    public string? RecipientAddressStreet { get; init; }
    public string? RecipientAddressCivic { get; init; }
    public string? RecipientPostalCode { get; init; }
    public string? RecipientCity { get; init; }
    public string? RecipientProvince { get; init; }
    public string? RecipientCountryCode { get; init; }
    public string? RecipientSdiCode { get; init; }
    public string? RecipientPec { get; init; }
    public decimal TaxableBase { get; init; }
    public decimal VatAmount { get; init; }
    public decimal StampDuty { get; init; }
    public decimal Total { get; init; }
    public decimal? VatRatePercent { get; init; }
    public string? VatNatureCode { get; init; }
    public string PaymentState { get; init; } = string.Empty;

    /// <summary>
    /// Who collected the money of the document (<c>FiscalPaymentChannel</c>, by name): <c>Unknown</c> until it is known, <c>CasaZen</c> (the guest paid in CasaZen), <c>Portal</c> (the
    /// portal collected the stay and pays it out) or <c>Outside</c> (the agency collected it outside CasaZen).
    /// </summary>
    public string PaymentChannel { get; init; } = string.Empty;

    /// <summary>
    /// What the portal collected of the document of a booking whose portal left the tourist tax to the host (FIX/BE-PORTAL-TAXLINE): the total less that tax, <c>450.00</c> of the <c>462.00</c> of
    /// a stay with <c>12.00</c> of tourist tax. Derived from the stored lines and from the collection of the stay (<see cref="FiscalDocumentPortalCollection"/>); <c>null</c>, and left out of the answer, for every other document
    /// (the list never has it: its rows carry no lines). <see cref="PaymentState"/> stays the stored one and <see cref="Total"/> is always the whole document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AmountPaidViaPortal { get; init; }

    /// <summary>
    /// The tourist tax the portal did not collect and the collection of the stay (E25) does not record as collected, to collect at the property (<c>12.00</c> in the example of <see cref="AmountPaidViaPortal"/>; <c>7.00</c> when 5.00 were paid online): the line of the tourist tax of the document
    /// (<see cref="FiscalDocumentLineDto.ToCollectAtProperty"/>). <c>null</c>, and left out of the answer, when <see cref="AmountPaidViaPortal"/> is.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AmountToCollectAtProperty { get; init; }

    public string SdiStatus { get; init; } = string.Empty;
    public string? SdiErrorCode { get; init; }
    public DateTime? SdiStatusUpdatedAt { get; init; }
    public bool HasXml { get; init; }
    public bool OctoratePdfAvailable { get; init; }
    public Guid? CreditedDocumentId { get; init; }
    public Guid? ReplacesDocumentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<FiscalDocumentLineDto> Lines { get; init; } = [];

    /// <summary>
    /// The issuer as the document keeps it (the snapshot written at the issue), in the answer for one document only. <c>null</c> for a draft, for a document issued before the snapshot existed (its PDF
    /// names the issuer from the profile of today) and in the rows of the list.
    /// </summary>
    public FiscalDocumentIssuerDto? Issuer { get; init; }

    /// <summary>The document as the answer for one document gives it: its lines and the issuer of its snapshot.</summary>
    public static FiscalDocumentDto From(FiscalDocument document) => Build(document, withIssuer: true);

    /// <summary>
    /// A document as a row of the list: the same members without the issuer (the list does not show it, and an issuer's address and tax ids leave the API one document at a time, to a caller who
    /// reaches that document).
    /// </summary>
    public static FiscalDocumentDto ForList(FiscalDocument document) => Build(document, withIssuer: false);

    private static FiscalDocumentDto Build(FiscalDocument document, bool withIssuer) =>
        Build(document, withIssuer, FiscalDocumentPortalCollection.SplitOf(document));

    private static FiscalDocumentDto Build(FiscalDocument document, bool withIssuer, PortalCollectionSplit? split) => new()
    {
        Id = document.Id,
        BookingId = document.BookingId,
        Source = document.Source.ToString(),
        Kind = document.Kind.ToString(),
        Purpose = document.Purpose.ToString(),
        Status = document.Status.ToString(),
        Series = string.IsNullOrEmpty(document.Series) ? null : document.Series,
        SequenceNumber = document.SequenceNumber,
        Number = document.Number,
        IssueDate = document.IssueDate,
        Currency = document.Currency,
        RecipientKind = document.RecipientKind?.ToString(),
        RecipientName = document.RecipientName,
        RecipientFiscalCode = document.RecipientFiscalCode,
        RecipientVatNumber = document.RecipientVatNumber,
        RecipientAddressStreet = document.RecipientAddressStreet,
        RecipientAddressCivic = document.RecipientAddressCivic,
        RecipientPostalCode = document.RecipientPostalCode,
        RecipientCity = document.RecipientCity,
        RecipientProvince = document.RecipientProvince,
        RecipientCountryCode = document.RecipientCountryCode,
        RecipientSdiCode = document.RecipientSdiCode,
        RecipientPec = document.RecipientPec,
        TaxableBase = document.TaxableBase,
        VatAmount = document.VatAmount,
        StampDuty = document.StampDuty,
        Total = document.Total,
        VatRatePercent = document.VatRatePercent,
        VatNatureCode = document.VatNatureCode,
        PaymentState = document.PaymentState.ToString(),
        PaymentChannel = document.PaymentChannel.ToString(),
        AmountPaidViaPortal = split?.PaidViaPortal,
        AmountToCollectAtProperty = split?.ToCollectAtTheProperty,
        SdiStatus = document.SdiStatus,
        SdiErrorCode = document.SdiErrorCode,
        SdiStatusUpdatedAt = document.SdiStatusUpdatedAt,
        HasXml = !string.IsNullOrEmpty(document.FatturaPaXml),
        OctoratePdfAvailable = document.OctoratePdfAvailable,
        CreditedDocumentId = document.CreditedDocumentId,
        ReplacesDocumentId = document.ReplacesDocumentId,
        CreatedAt = document.CreatedAt,
        UpdatedAt = document.UpdatedAt,
        Lines = document.Lines.OrderBy(l => l.LineNumber).Select(l => FiscalDocumentLineDto.From(l, split?.IsToCollect(l) == true)).ToList(),
        Issuer = withIssuer ? FiscalDocumentIssuerDto.From(document.IssuerSnapshot) : null,
    };
}

/// <summary>
/// A line of a fiscal document: what it is (<see cref="LineType"/>, the name of <c>FiscalLineType</c>: <c>Accommodation</c>, <c>Cleaning</c>, <c>ExtraService</c>, <c>TouristTax</c>, <c>Commission</c>,
/// <c>Recharge</c>, <c>AdvancedExpense</c> or <c>Other</c>), its amounts net of VAT, its VAT rate and nature code, and the VAT of the line as it was computed (<see cref="VatAmount"/>; <c>null</c> for a line
/// written before it was stored, which the reader then works out from its rate). <see cref="ToCollectAtProperty"/> is true for the line of tourist tax a portal left to the host, which the guest pays at the
/// property and the document does not declare paid (FIX/BE-PORTAL-TAXLINE); it is left out of the answer for every other line.
/// </summary>
public sealed record FiscalDocumentLineDto(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    decimal VatRatePercent,
    string? VatNatureCode,
    string LineType,
    decimal? VatAmount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ToCollectAtProperty = false)
{
    public static FiscalDocumentLineDto From(FiscalDocumentLine line, bool toCollectAtProperty = false) =>
        new(line.LineNumber, line.Description, line.Quantity, line.UnitPrice, line.TotalPrice, line.VatRatePercent, line.VatNatureCode, line.LineType.ToString(), line.VatAmount, toCollectAtProperty);
}

/// <summary>
/// The issuer of a document as it was when the document was issued (FIX/RES-DOCS): the snapshot the document keeps (encrypted at rest), as the PDF and the XML print it. The name is the one the
/// document prints for the issuer (its legal name, else the name of the organization); the tax ids are the Italian VAT number and fiscal code, and the Swiss VAT number (UID) of a Swiss issuer. The
/// bank account (not even masked), the agency that issued on behalf of an owner and the co-owners stay in the PDF. The names are the ones the drawer of the frontend reads: <c>legalName</c>,
/// <c>vatNumber</c> or <c>swissVatNumber</c>, <c>countryCode</c>.
/// </summary>
public sealed record FiscalDocumentIssuerDto(
    string? LegalName,
    string? AddressStreet,
    string? AddressCivic,
    string? PostalCode,
    string? City,
    string? Province,
    string CountryCode,
    string? VatNumber,
    string? FiscalCode,
    string? SwissVatNumber)
{
    /// <summary>The issuer of a stored snapshot; <c>null</c> when there is none or it cannot be read: nothing is looked up and nothing is made up.</summary>
    public static FiscalDocumentIssuerDto? From(string? snapshotJson)
    {
        var snapshot = FiscalIssuerSnapshot.FromJson(snapshotJson);
        if (snapshot is null)
            return null;

        var name = snapshot.DisplayName;
        return new FiscalDocumentIssuerDto(
            string.IsNullOrWhiteSpace(name) ? null : name,
            snapshot.AddressStreet,
            snapshot.AddressCivic,
            snapshot.PostalCode,
            snapshot.City,
            snapshot.Province,
            snapshot.CountryCode,
            snapshot.VatNumber,
            snapshot.FiscalCode,
            snapshot.SwissVatNumber);
    }
}

/// <summary>A page of the list of the documents.</summary>
public sealed record FiscalDocumentPageDto(IReadOnlyList<FiscalDocumentDto> Items, int TotalCount, int Page, int PageSize);

/// <summary><c>POST api/fiscal/documents</c>: the draft of a booking.</summary>
public sealed record CreateFiscalDocumentRequest
{
    public Guid BookingId { get; init; }
}

/// <summary><c>POST api/fiscal/documents/{id}/issue</c>: the host confirms the issue.</summary>
public sealed record IssueFiscalDocumentRequest
{
    public bool Confirm { get; init; }
}

/// <summary>
/// <c>POST api/fiscal/documents/issue-batch</c> (E12/F01/US01/T02): the drafts to issue. <see cref="DocumentIds"/> names them; <see cref="From"/>/<see cref="To"/> (Europe/Rome
/// days of the check-out of the stay, both ends included) and <see cref="PropertyIds"/> select the drafts of a period and of some properties. At most 200 documents; an empty
/// selection is a 422, never «all».
/// </summary>
public sealed record IssueFiscalDocumentsBatchRequest
{
    public IReadOnlyList<Guid>? DocumentIds { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public IReadOnlyList<Guid>? PropertyIds { get; init; }
}

/// <summary>One document of a bulk issue: issued with its number, or not with the stable code of why (<c>errorCode</c>).</summary>
public sealed record FiscalDocumentBatchRowDto(Guid Id, bool Issued, string? Number, string? ErrorCode);

/// <summary>
/// The answer of a bulk issue: one row per document, how many were issued and how many were not (never «all issued» while one failed), and how many more drafts the selection
/// matched beyond the 200 of the request.
/// </summary>
public sealed record FiscalDocumentBatchResultDto(IReadOnlyList<FiscalDocumentBatchRowDto> Rows, int Issued, int Failed, int Remaining)
{
    public static FiscalDocumentBatchResultDto From(Casazen.Core.Services.FiscalDocumentBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new FiscalDocumentBatchResultDto(
            result.Rows.Select(r => new FiscalDocumentBatchRowDto(r.Id, r.Issued, r.Number, r.ErrorCode)).ToList(),
            result.Issued,
            result.Failed,
            result.Remaining);
    }
}

/// <summary>
/// One document of a booking as its card shows it (<c>GET api/bookings/{id}/fiscal-documents</c>, E12/F01/US01/T02): number, kind, status, total, currency and payment state
/// (the current one), with the day of the issue and, for a credit note, the document it credits. No recipient and no line.
/// </summary>
public sealed record BookingFiscalDocumentDto(
    Guid Id,
    string Kind,
    string Status,
    string? Number,
    decimal Total,
    string Currency,
    string PaymentState,
    DateOnly? IssueDate,
    Guid? CreditedDocumentId)
{
    public static BookingFiscalDocumentDto From(FiscalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new BookingFiscalDocumentDto(
            document.Id,
            document.Kind.ToString(),
            document.Status.ToString(),
            document.Number,
            document.Total,
            document.Currency,
            document.PaymentState.ToString(),
            document.IssueDate,
            document.CreditedDocumentId);
    }
}

/// <summary><c>POST api/fiscal/documents/{id}/credit-note</c>: the amount (the whole document when left out), the reason and the confirmation.</summary>
public sealed record CreateCreditNoteRequest
{
    public decimal? Amount { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool Confirm { get; init; }
}
