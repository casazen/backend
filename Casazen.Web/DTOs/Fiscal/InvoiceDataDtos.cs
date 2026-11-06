using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// The invoice data of a booking as the host sees it (FIS-01, NF-81): who the invoice is for, where the data came from and whether the guest said
/// they need an invoice. A booking with no data answers the empty object: its id and <c>invoiceRequested: false</c>, and nothing else.
/// </summary>
public sealed class InvoiceDataDto
{
    public Guid BookingId { get; init; }

    /// <summary>The guest said «Ti serve la fattura?» yes at the check-in.</summary>
    public bool InvoiceRequested { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? InvoiceRequestedAt { get; init; }

    /// <summary><c>Guest</c> or <c>Company</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecipientKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FiscalCode { get; init; }

    /// <summary>With the country prefix, for example <c>IT12345678903</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VatNumber { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AddressStreet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AddressCivic { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostalCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Province { get; init; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryCode { get; init; }

    /// <summary>The code of the recipient on the SDI (7 letters or digits).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdiRecipientCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pec { get; init; }

    /// <summary>The highest source that wrote the data: <c>Octorate</c>, <c>CheckIn</c> or <c>Host</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; init; }

    /// <summary>The empty object of a booking that has no invoice data.</summary>
    public static InvoiceDataDto Empty(Guid bookingId) => new() { BookingId = bookingId };

    public static InvoiceDataDto From(InvoiceDataView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new InvoiceDataDto
        {
            BookingId = view.BookingId,
            InvoiceRequested = view.InvoiceRequested,
            InvoiceRequestedAt = view.InvoiceRequestedAt,
            RecipientKind = view.RecipientKind.ToString(),
            Name = view.Name,
            FiscalCode = view.FiscalCode,
            VatNumber = view.VatNumber,
            AddressStreet = view.AddressStreet,
            AddressCivic = view.AddressCivic,
            PostalCode = view.PostalCode,
            City = view.City,
            Province = view.Province,
            CountryCode = view.CountryCode,
            SdiRecipientCode = view.SdiRecipientCode,
            Pec = view.Pec,
            Source = view.Source.ToString(),
            UpdatedAt = view.UpdatedAt,
        };
    }
}

/// <summary>
/// What the host saves of the invoice data of a booking (<c>PUT /api/bookings/{bookingId}/invoice-data</c>). A member that is left out (<c>null</c>)
/// keeps what is stored; one that is sent empty clears it. The formats are checked by the service (422 with the code and the field of each failure);
/// the booking, the org and the source (<c>Host</c>) are never taken from the body.
/// </summary>
public sealed class SaveInvoiceDataRequest
{
    /// <summary><c>Guest</c> or <c>Company</c>.</summary>
    public string? RecipientKind { get; init; }

    public string? Name { get; init; }

    public string? FiscalCode { get; init; }

    public string? VatNumber { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    public string? SdiRecipientCode { get; init; }

    public string? Pec { get; init; }

    /// <summary>The command of the service. A kind of recipient that is neither <c>Guest</c> nor <c>Company</c> is 422 <c>invoice_data_field_invalid</c> on <c>recipientKind</c>.</summary>
    public SaveInvoiceDataCommand ToCommand()
    {
        InvoiceRecipientKind? kind = null;
        if (RecipientKind is not null)
        {
            if (!Enum.TryParse<InvoiceRecipientKind>(RecipientKind.Trim(), ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed)
                || RecipientKind.Trim().All(char.IsAsciiDigit))
            {
                throw InvoiceDataErrorCodes.ToException(
                    [new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.RecipientKind)]);
            }

            kind = parsed;
        }

        return new SaveInvoiceDataCommand
        {
            RecipientKind = kind,
            Name = Name,
            FiscalCode = FiscalCode,
            VatNumber = VatNumber,
            AddressStreet = AddressStreet,
            AddressCivic = AddressCivic,
            PostalCode = PostalCode,
            City = City,
            Province = Province,
            CountryCode = CountryCode,
            SdiRecipientCode = SdiRecipientCode,
            Pec = Pec,
        };
    }
}
