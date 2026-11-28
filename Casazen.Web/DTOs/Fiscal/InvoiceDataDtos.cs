using System.Text.Json.Serialization;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

public sealed class InvoiceDataDto
{
    public Guid BookingId { get; init; }

    /// <summary>The guest said «Ti serve la fattura?» yes at the check-in.</summary>
    public bool InvoiceRequested { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? InvoiceRequestedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecipientKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FiscalCode { get; init; }

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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdiRecipientCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pec { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; init; }

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

public sealed class SaveInvoiceDataRequest
{
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

    public SaveInvoiceDataCommand ToCommand()
    {
        if (!InvoiceDataFormats.TryParseRecipientKind(RecipientKind, out var kind))
        {
            throw InvoiceDataErrorCodes.ToException(
                [new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.RecipientKind)]);
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
