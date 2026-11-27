using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The collections of a reservation (FIS-05, E06/F04/US01/T03): the <c>payments</c> of the single read of the reservation
/// (<c>GET /rest/v1/reservation/{acc}/{id}</c>), with who collects for it (<c>companyCollect</c>). Same HTTPS client, tenant isolation, rate-limit gate and
/// typed errors as every other call of <see cref="OctorateApiClient"/>.
/// </summary>
/// <remarks>
/// The single read with a payment was seen in the sandbox (a direct booking with one payment, S25); the members of a payment (<c>amount</c>,
/// <c>date</c>, <c>paymentMode</c>) are the OpenAPI's (<c>ApiReservationPaymentDTO</c>) and <c>companyCollect</c> was absent on the sandbox OTA samples:
/// a member that is not there is <c>null</c>, never a default.
/// </remarks>
public sealed partial class OctorateApiClient
{
    public async Task<IReadOnlyList<OctoratePayment>> GetReservationPaymentsAsync(string accommodationId, string reservationId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        if (string.IsNullOrWhiteSpace(reservationId))
            throw new ArgumentException("A reservation id is required.", nameof(reservationId));

        using var answer = await GetJsonAsync(
            $"/rest/v1/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reservationId.Trim())}",
            "reservation-payments",
            ct);
        return PaymentsOf(answer.RootElement);
    }

    /// <summary>The payments of a single read (a bare reservation, or one wrapped in <c>data</c>); an item without an amount is left out.</summary>
    internal static IReadOnlyList<OctoratePayment> PaymentsOf(JsonElement root)
    {
        var reservation = root.ValueKind == JsonValueKind.Object && OctorateJson.Find(root, "data") is { ValueKind: JsonValueKind.Object } data ? data : root;
        var companyCollect = OctorateJson.Text(reservation, "companyCollect");
        if (OctorateJson.Find(reservation, "payments") is not { ValueKind: JsonValueKind.Array } payments)
            return [];

        var list = new List<OctoratePayment>();
        foreach (var item in payments.EnumerateArray())
        {
            if (OctorateJson.Decimal(item, "amount", "total") is not { } amount)
                continue;

            var date = OctorateJson.DateOf(OctorateJson.Text(item, "date", "paymentDate"))
                ?? (OctorateJson.Instant(item, "createTime", "created") is { } instant ? DateOnly.FromDateTime(instant) : null);
            list.Add(new OctoratePayment(amount, date, OctorateJson.Text(item, "paymentMode", "mode", "type"), companyCollect, OctorateJson.Text(item, "transaction")));
        }

        return list;
    }
}
