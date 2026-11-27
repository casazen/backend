using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Who an invoice of a booking is made out to (FIS-01, NF-81, E06/F02/US01/T01): the name or denomination, the fiscal code, the VAT number, the
/// address, the code of the recipient on the SDI or the PEC, whether the guest said «Ti serve la fattura?», and where the data came from. At
/// most one row for each booking (unique <see cref="BookingId"/>); it goes with the booking.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Personal data, encrypted.</b> <see cref="Name"/>, <see cref="FiscalCode"/>, <see cref="VatNumber"/>, <see cref="AddressStreet"/>,
/// <see cref="AddressCivic"/>, <see cref="PostalCode"/>, <see cref="City"/> and <see cref="Pec"/> are encrypted at rest (<c>EncryptedColumns</c>,
/// purpose <c>Casazen.BookingInvoiceData</c>): the code reads and writes the clear value through EF, the database only holds the payload, so these
/// columns carry no length (the payload is longer than the text) and the lengths below are checked on input. Nothing is searched in them. So is
/// <see cref="OctorateBillingHash"/>, the hash of what was last written to Octorate: a hash of personal data is personal data.</item>
/// <item><b>Precedence.</b> <see cref="Source"/> is the highest source that ever wrote the row. A write from a lower source fills only the empty
/// fields (<see cref="InvoiceDataPrecedence"/>), so a host's edit is never overwritten by an import or by the guest.</item>
/// <item><b>Not copied from elsewhere.</b> The row never holds an identity document or any Alloggiati data: only what an invoice needs.</item>
/// <item><b>Concurrency.</b> <see cref="RowVersion"/> is PostgreSQL's <c>xmin</c>: two writers of the same row cannot both win, so the loser reads
/// the row again and applies the precedence to the version that was saved first.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking, set by the service and never taken from a client.</item>
/// </list>
/// </remarks>
[Table("BookingInvoiceData")]
public class BookingInvoiceData : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="BookingId"/>: one row of invoice data for each booking.</summary>
    public const string BookingIndexName = "UIX_BookingInvoiceData_BookingId";

    /// <summary>Longest <see cref="Name"/>: the denomination of a company or «First Last».</summary>
    public const int NameMaxLength = 200;

    /// <summary>Longest <see cref="FiscalCode"/>.</summary>
    public const int FiscalCodeMaxLength = 16;

    /// <summary>Longest <see cref="VatNumber"/>: the country prefix and the number (IT and 11 digits for an Italian one).</summary>
    public const int VatNumberMaxLength = 28;

    /// <summary>Longest <see cref="AddressStreet"/>.</summary>
    public const int AddressStreetMaxLength = 200;

    /// <summary>Longest <see cref="AddressCivic"/>.</summary>
    public const int AddressCivicMaxLength = 20;

    /// <summary>Longest <see cref="PostalCode"/>.</summary>
    public const int PostalCodeMaxLength = 10;

    /// <summary>Longest <see cref="City"/>.</summary>
    public const int CityMaxLength = 100;

    /// <summary>Length of <see cref="Province"/>: the two letters of an Italian province.</summary>
    public const int ProvinceMaxLength = 2;

    /// <summary>Length of <see cref="CountryCode"/>: ISO 3166-1 alpha-2.</summary>
    public const int CountryCodeMaxLength = 2;

    /// <summary>Length of <see cref="SdiRecipientCode"/>: the code of the recipient on the SDI, 7 letters or digits.</summary>
    public const int SdiRecipientCodeMaxLength = 7;

    /// <summary>Longest <see cref="Pec"/>: an e-mail address.</summary>
    public const int PecMaxLength = 254;

    /// <summary>Longest <see cref="UpdatedByUserId"/>.</summary>
    public const int UpdatedByUserIdMaxLength = 255;

    /// <summary>Longest <see cref="OctorateBillingError"/>: a code, never a text.</summary>
    public const int OctorateBillingErrorMaxLength = 64;

    /// <summary>The country of a recipient that does not say: Italy.</summary>
    public const string DefaultCountryCode = "IT";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2). Inherited from the booking; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking the data is for. Unique: a booking has at most one row. The row goes with the booking (cascade).</summary>
    public Guid BookingId { get; set; }

    /// <summary>The guest said «I need an invoice» (the question of the public check-in). False until they do.</summary>
    public bool InvoiceRequested { get; set; }

    /// <summary>When the guest said it (UTC); <c>null</c> while <see cref="InvoiceRequested"/> is false.</summary>
    public DateTime? InvoiceRequestedAt { get; set; }

    /// <summary>Whether the invoice is for a private person or for a company.</summary>
    public InvoiceRecipientKind RecipientKind { get; set; } = InvoiceRecipientKind.Guest;

    /// <summary>The denomination of the company, or «First Last». <b>Encrypted at rest</b> (no length on the column; <see cref="NameMaxLength"/> is checked on input).</summary>
    public string? Name { get; set; }

    /// <summary>Codice fiscale of the recipient (normalized: upper case, no spaces; <see cref="FiscalCodeMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? FiscalCode { get; set; }

    /// <summary>VAT number with the country prefix, for example <c>IT</c> and 11 digits (<see cref="VatNumberMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? VatNumber { get; set; }

    /// <summary>Street of the address (<see cref="AddressStreetMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? AddressStreet { get; set; }

    /// <summary>House number of the address (<see cref="AddressCivicMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? AddressCivic { get; set; }

    /// <summary>Postal code (<see cref="PostalCodeMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? PostalCode { get; set; }

    /// <summary>City or comune (<see cref="CityMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? City { get; set; }

    /// <summary>The two letters of the Italian province; <c>null</c> for a recipient abroad or when not given.</summary>
    [MaxLength(ProvinceMaxLength)]
    public string? Province { get; set; }

    /// <summary>ISO 3166-1 alpha-2 code of the country of the recipient; <c>IT</c> by default.</summary>
    [Required, MaxLength(CountryCodeMaxLength)]
    public string CountryCode { get; set; } = DefaultCountryCode;

    /// <summary>The code of the recipient on the SDI (7 letters or digits, upper case); <c>null</c> when the recipient has none.</summary>
    [MaxLength(SdiRecipientCodeMaxLength)]
    public string? SdiRecipientCode { get; set; }

    /// <summary>The certified e-mail (PEC) of the recipient, lower case (<see cref="PecMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? Pec { get; set; }

    /// <summary>The highest source that ever wrote the row: it decides whether a later write may overwrite a field or only fill an empty one.</summary>
    public InvoiceDataSource Source { get; set; }

    /// <summary>
    /// The hash of the billing data last written to Octorate (E06/F02/US01/T04): what makes the write-back happen once for each change, because the data
    /// is not sent again while its hash is the one stored here. A hash of personal data is personal data, so it is <b>encrypted at rest</b> like the data
    /// itself (no length on the column). <c>null</c> until a write succeeded; never part of an answer of the API.
    /// </summary>
    public string? OctorateBillingHash { get; set; }

    /// <summary>When the billing data was last written to Octorate (UTC); <c>null</c> until it was.</summary>
    public DateTime? OctorateBillingSentAt { get; set; }

    /// <summary>
    /// The code of the last failure of the write of the billing data to Octorate (E06/F02/US01/T04): a stable code, the one of the error Octorate gave
    /// (<c>octorate_not_found</c>, <c>octorate_request_rejected</c>, <c>octorate_unavailable</c>...) or <c>octorate_no_customer_record</c> when the
    /// reservation names no record to write to; never a value of the data, so it is not personal data and not encrypted. It is what a person who asks
    /// why a booking's data is not on Octorate reads. <c>null</c> until a write failed, and again once one succeeded.
    /// </summary>
    [MaxLength(OctorateBillingErrorMaxLength)]
    public string? OctorateBillingError { get; set; }

    /// <summary>When the last failure of the write to Octorate happened (UTC); <c>null</c> exactly when <see cref="OctorateBillingError"/> is.</summary>
    public DateTime? OctorateBillingErrorAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who saved the data last, when it was a person (the host); <c>null</c> for the guest and the channel manager.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column of its
    /// own), so two writers of the same row cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }
}
