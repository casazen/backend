using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Reads a reservation (<c>ApiReservationRespDTO</c>) and the list that holds it from the answer of Octorate (E02/F02/US01/T01). The names
/// are those the sandbox runs recorded (<c>kb/plan/octorate-integration-facts.md</c>): <c>id</c>, <c>refer</c>, <c>channelId</c>, <c>channelName</c>,
/// <c>channelRefer</c>, <c>otaId</c>, <c>status</c>, <c>checkin</c>, <c>checkout</c>, <c>roomGross</c>, <c>totalGross</c>, <c>touristTax</c>,
/// <c>totalGuest</c>, <c>paymentStatus</c>, <c>product</c>, <c>pmsProduct</c>, <c>firstName</c>, <c>lastName</c>, <c>updateTime</c> and, in the
/// single read, <c>totalPaid</c> and <c>priceBreakdown</c>; the <c>currency</c> is, in the OpenAPI, an object with a <c>currencyCode</c>.
/// </summary>
/// <remarks>
/// A member that is not there is <c>null</c>, never a made-up value: an amount Octorate does not give stays unknown. The private notes of
/// the host are never read. The contact values of a guest are read only if the answer carries them (the sandbox gives flags only).
/// </remarks>
internal static class OctorateReservationReader
{
    /// <summary>Longest e-mail kept (the column of the guest).</summary>
    private const int MaxEmailLength = 255;

    /// <summary>Longest phone kept (the column of the guest).</summary>
    private const int MaxPhoneLength = 20;

    private static readonly string[] ContainerNames = ["data", "content", "items", "reservations", "results", "result"];

    /// <summary>
    /// The reservations of a list answer: a bare array (v1) or the array of a wrapper (<c>{data, page}</c>). <c>null</c> when the answer is
    /// neither, so a caller can tell an empty list from an answer it cannot read.
    /// </summary>
    internal static IEnumerable<JsonElement>? ListItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in ContainerNames)
        {
            if (OctorateJson.Find(root, name) is { ValueKind: JsonValueKind.Array } array)
                return array.EnumerateArray();
        }

        return null;
    }

    /// <summary>
    /// The page a list answer holds: the reservations that could be read, and how many items the answer had. <c>null</c> when the answer is not a
    /// list (<see cref="ListItems"/>). An item that is not an object, or has no usable id, is left out of the reservations but still counted: a
    /// caller that decided where the list ends on the number of reservations read would stop at a page that only looks short and lose the pages
    /// after it.
    /// </summary>
    internal static OctorateReservationPage? ReadPage(JsonElement root)
    {
        if (ListItems(root) is not { } items)
            return null;

        var listed = items.ToList();
        return new OctorateReservationPage([.. listed.Select(item => Read(item)).OfType<OctorateReservation>()], listed.Count);
    }

    /// <summary>The reservation of <paramref name="item"/>, or <c>null</c> when it is not an object or has no usable id.</summary>
    /// <param name="item">One reservation of an answer.</param>
    /// <param name="fallbackId">The id the call asked for, used when the answer does not repeat it.</param>
    internal static OctorateReservation? Read(JsonElement item, string? fallbackId = null)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateJson.Text(item, "id", "reservationId") ?? fallbackId;
        if (id is null || id.Length > Booking.OctorateReservationIdMaxLength)
            return null;

        var guests = OctorateJson.Find(item, "guests") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object).ToList()
            : [];
        var firstGuest = guests.Count > 0 ? guests[0] : default;

        return new OctorateReservation
        {
            Id = id,
            Refer = OctorateJson.Text(item, "refer"),
            ChannelId = OctorateJson.Text(item, "channelId"),
            ChannelName = OctorateJson.Text(item, "channelName"),
            ChannelRefer = OctorateJson.Text(item, "channelRefer"),
            OtaId = OctorateJson.Text(item, "otaId"),
            Status = StatusOf(OctorateJson.Text(item, "status")),
            Product = OctorateJson.Text(item, "product", "productId"),
            PmsProduct = OctorateJson.Text(item, "pmsProduct"),
            CheckIn = OctorateJson.Text(item, "checkin", "checkIn"),
            CheckOut = OctorateJson.Text(item, "checkout", "checkOut"),
            UpdatedAt = OctorateJson.Instant(item, "updateTime", "updatedAt"),
            CancelledAt = OctorateJson.Instant(item, "cancelledTime", "cancelledAt"),
            TotalGuests = OctorateJson.Int(item, "totalGuest", "totalGuests"),
            Adults = OctorateJson.Int(item, "adults"),
            Children = OctorateJson.Int(item, "children"),
            Infants = OctorateJson.Int(item, "infants"),
            FirstName = OctorateJson.Text(item, "firstName") ?? OctorateJson.Text(firstGuest, "firstName"),
            LastName = OctorateJson.Text(item, "lastName") ?? OctorateJson.Text(firstGuest, "lastName"),
            GuestEmail = EmailOf(OctorateJson.Text(item, "email", "guestEmail") ?? OctorateJson.Text(firstGuest, "email")),
            GuestPhone = PhoneOf(OctorateJson.Text(item, "phone", "guestPhone") ?? OctorateJson.Text(firstGuest, "phone")),
            // Only what the guest wrote: never the private notes of the host.
            Notes = OctorateJson.Text(item, "guestNotes", "guestNote", "specialRequests", "comments", "comment"),
            Currency = CurrencyOf(item),
            RoomGross = OctorateJson.Decimal(item, "roomGross"),
            TotalGross = OctorateJson.Decimal(item, "totalGross"),
            TouristTax = OctorateJson.Decimal(item, "touristTax"),
            TotalPaid = OctorateJson.Decimal(item, "totalPaid"),
            PaymentStatus = OctorateJson.Text(item, "paymentStatus"),
            PriceBreakdown = PriceBreakdownOf(item),
            PaymentTransactions = PaymentTransactionsOf(item),
        };
    }

    /// <summary>The <c>transaction</c> of each payment of the single read (<c>payments[].transaction</c>, <c>ApiReservationPaymentDTO</c>); never an amount or a card.</summary>
    private static IReadOnlyList<string> PaymentTransactionsOf(JsonElement item)
    {
        if (OctorateJson.Find(item, "payments") is not { ValueKind: JsonValueKind.Array } payments)
            return [];

        return
        [
            .. payments.EnumerateArray()
                .Where(payment => payment.ValueKind == JsonValueKind.Object)
                .Select(payment => OctorateJson.Text(payment, "transaction"))
                .OfType<string>(),
        ];
    }

    /// <summary>The status Octorate wrote, or <c>null</c> for a word that is none of the three.</summary>
    internal static OctorateBookingStatus? StatusOf(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "WAITING" => OctorateBookingStatus.Waiting,
        "CONFIRMED" => OctorateBookingStatus.Confirmed,
        "CANCELLED" or "CANCELED" => OctorateBookingStatus.Cancelled,
        _ => null,
    };

    private static IReadOnlyList<OctoratePriceComponent> PriceBreakdownOf(JsonElement item)
    {
        if (OctorateJson.Find(item, "priceBreakdown") is not { ValueKind: JsonValueKind.Array } lines)
            return [];

        var components = new List<OctoratePriceComponent>();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object || OctorateJson.Text(line, "type") is not { } type)
                continue;

            components.Add(new OctoratePriceComponent(type, OctorateJson.Decimal(line, "amount", "value", "price", "total")));
        }

        return components;
    }

    private static string? EmailOf(string? text) =>
        text is { Length: > 0 and <= MaxEmailLength } && text.Contains('@') && !text.Any(char.IsWhiteSpace) ? text : null;

    private static string? PhoneOf(string? text) => text is { Length: > 0 and <= MaxPhoneLength } ? text : null;

    /// <summary>
    /// The ISO code of the currency of the reservation. <c>ApiReservationRespDTO.currency</c> is an object (<c>{ "currencyCode": "EUR", "numericCode":
    /// 978, ... }</c>, OpenAPI 2026-11-04); the plain code as a string is read as well. <c>null</c> when the answer states none, or something that is
    /// not a three-letter code: the amounts of such a reservation are not taken for the property's.
    /// </summary>
    private static string? CurrencyOf(JsonElement item)
    {
        if (OctorateJson.Find(item, "currency", "currencyCode") is not { } value)
            return null;

        var text = value.ValueKind == JsonValueKind.Object ? OctorateJson.Text(value, "currencyCode") : OctorateJson.TextOf(value);
        return text is { Length: 3 } && text.All(char.IsAsciiLetter) ? text.ToUpperInvariant() : null;
    }
}
