using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Everything a customer typed for a request on measure from a supplier's showcase, and what the server worked out from it (SP-10b):
/// the content of <c>ShowcaseInquiry.Payload</c>. It is serialized to JSON and <b>encrypted at rest</b> by the column
/// (<c>EncryptedColumns</c>); when the e-mail is checked it is split between the <c>ServiceCustomer</c> (name, e-mail, phone, language,
/// consent), the <c>ServiceRequest</c> (service, place) and the first <c>ServiceRequestMessage</c> (the words), and the inquiry forgets it.
/// </summary>
/// <param name="ServiceListingId">The catalog service the request is about, when it is about one.</param>
/// <param name="ServiceName">Its name at the time of the request (<c>ServiceRequest.ServiceNameSnapshot</c>).</param>
/// <param name="Category">The category code of the request: the service's, else the supplier's first one.</param>
/// <param name="Message">What the customer needs, in its own words.</param>
/// <param name="ComuneIstat">ISTAT code of the comune, when the customer chose it from the official list.</param>
/// <param name="ConsentIp">The client address the privacy notice was accepted from (consent evidence).</param>
public sealed record ShowcaseInquiryPayload(
    Guid? ServiceListingId,
    string? ServiceName,
    string Category,
    string FullName,
    string Email,
    string Phone,
    string Locale,
    string? ComuneIstat,
    string City,
    string Message,
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
    public static ShowcaseInquiryPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ShowcaseInquiryPayload>(json, Json);
        }
        catch (JsonException)
        {
            // Written by this module only; an unreadable value is treated as missing rather than failing the whole check.
            return null;
        }
    }
}
