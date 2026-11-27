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
    public const string BookingIndexName = "UIX_BookingInvoiceData_BookingId";

    public const int NameMaxLength = 200;

    public const int FiscalCodeMaxLength = 16;

    public const int VatNumberMaxLength = 28;

    public const int AddressStreetMaxLength = 200;

    public const int AddressCivicMaxLength = 20;

    public const int PostalCodeMaxLength = 10;

    public const int CityMaxLength = 100;

    public const int ProvinceMaxLength = 2;

    public const int CountryCodeMaxLength = 2;

    /// <summary>Length of <see cref="SdiRecipientCode"/>: the code of the recipient on the SDI, 7 letters or digits.</summary>
    public const int SdiRecipientCodeMaxLength = 7;

    public const int PecMaxLength = 254;

    public const int UpdatedByUserIdMaxLength = 255;

    public const int OctorateBillingErrorMaxLength = 64;

    public const string DefaultCountryCode = "IT";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public bool InvoiceRequested { get; set; }

    public DateTime? InvoiceRequestedAt { get; set; }

    public InvoiceRecipientKind RecipientKind { get; set; } = InvoiceRecipientKind.Guest;

    public string? Name { get; set; }

    /// <summary>Codice fiscale of the recipient (normalized: upper case, no spaces; <see cref="FiscalCodeMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? FiscalCode { get; set; }

    /// <summary>VAT number with the country prefix, for example <c>IT</c> and 11 digits (<see cref="VatNumberMaxLength"/>). <b>Encrypted at rest.</b></summary>
    public string? VatNumber { get; set; }

    public string? AddressStreet { get; set; }

    public string? AddressCivic { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    [MaxLength(ProvinceMaxLength)]
    public string? Province { get; set; }

    [Required, MaxLength(CountryCodeMaxLength)]
    public string CountryCode { get; set; } = DefaultCountryCode;

    /// <summary>The code of the recipient on the SDI (7 letters or digits, upper case); <c>null</c> when the recipient has none.</summary>
    [MaxLength(SdiRecipientCodeMaxLength)]
    public string? SdiRecipientCode { get; set; }

    public string? Pec { get; set; }

    public InvoiceDataSource Source { get; set; }

    /// <summary>
    /// The hash of the billing data last written to Octorate (E06/F02/US01/T04): what makes the write-back happen once for each change, because the data
    /// is not sent again while its hash is the one stored here. A hash of personal data is personal data, so it is <b>encrypted at rest</b> like the data
    /// itself (no length on the column). <c>null</c> until a write succeeded; never part of an answer of the API.
    /// </summary>
    public string? OctorateBillingHash { get; set; }

    public DateTime? OctorateBillingSentAt { get; set; }

    /// <summary>
    /// The code of the last failure of the write of the billing data to Octorate (E06/F02/US01/T04): a stable code, the one of the error Octorate gave
    /// (<c>octorate_not_found</c>, <c>octorate_request_rejected</c>, <c>octorate_unavailable</c>...) or <c>octorate_no_customer_record</c> when the
    /// reservation names no record to write to; never a value of the data, so it is not personal data and not encrypted. It is what a person who asks
    /// why a booking's data is not on Octorate reads. <c>null</c> until a write failed, and again once one succeeded.
    /// </summary>
    [MaxLength(OctorateBillingErrorMaxLength)]
    public string? OctorateBillingError { get; set; }

    public DateTime? OctorateBillingErrorAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column of its
    /// own), so two writers of the same row cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }
}
