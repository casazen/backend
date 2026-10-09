using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Everything a customer typed for a booking from a supplier's showcase, and what the server worked out from it (SP-10): the
/// content of <c>ShowcaseBookingHold.Payload</c>. It is serialized to JSON and <b>encrypted at rest</b> by the column
/// (<c>EncryptedColumns</c>); when the e-mail is checked it is split between the <c>ServiceCustomer</c> (name, e-mail, phone,
/// language, consent) and the <c>ServiceRequest</c> (service, price, place) and the hold forgets it.
/// </summary>
/// <param name="ServiceListingId">The catalog service the customer booked (<c>ServiceRequest.ServiceListingId</c>).</param>
/// <param name="ServiceName">Its name at the time of the booking (<c>ServiceRequest.ServiceNameSnapshot</c>).</param>
/// <param name="Category">Its category code (<c>ServiceRequest.Category</c>).</param>
/// <param name="EstimatedAmountCents">
/// The estimate the customer saw (<see cref="SupplierQuoteCalculator"/>, the same function that priced it), in cents of euro;
/// <c>null</c> when the service is on quote or has no price.
/// </param>
/// <param name="Options">The supplements the customer picked and the quantity, as a snapshot (<c>ServiceRequest.OptionsJson</c>).</param>
/// <param name="ComuneIstat">ISTAT code of the comune, when the customer chose it from the official list.</param>
/// <param name="ConsentIp">The client address the privacy notice was accepted from (consent evidence).</param>
public sealed record ShowcaseBookingPayload(
    Guid ServiceListingId,
    string ServiceName,
    string Category,
    int? EstimatedAmountCents,
    IReadOnlyList<ServiceRequestOption> Options,
    string FullName,
    string Email,
    string Phone,
    string Locale,
    string? ComuneIstat,
    string City,
    string PostalCode,
    string Address,
    string? Floor,
    string? AccessNotes,
    string PrivacyNoticeVersion,
    DateTime PrivacyAcceptedAt,
    string ConsentIp)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The payload as the JSON that is stored (and then encrypted by the column).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The payload stored in <paramref name="json"/>; <c>null</c> for a missing or unreadable value.</summary>
    public static ShowcaseBookingPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ShowcaseBookingPayload>(json, Json);
        }
        catch (JsonException)
        {
            // Written by this module only; an unreadable value is treated as missing rather than failing the whole check.
            return null;
        }
    }
}
