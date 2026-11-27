using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The bodies CasaZen sends to create a reservation and to record a payment on Octorate (E08/F01/US01/T02, D9, D13), with the names and the
/// forms the sandbox accepted on 2026-11-04 (run 5: channel 270, the unit with its PMS room, then a payment with an external transaction id)
/// and the required members of the OpenAPI (<c>createSingleReservation</c>, <c>addPaymentReservation</c>). Pure: no call, no clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No automatic charge</b> (D9): no <c>scheduled*</c>, <c>preauth*</c>, <c>stopCron</c>, <c>creditCard</c>, <c>paymentStep</c> or
/// <c>deposit</c> is ever written; Octorate never charges a card for CasaZen.</item>
/// <item><b>Amounts</b> to the cent: <c>roomGross</c> is the booking's price without the tourist tax (lodging and cleaning, the figure the import
/// reads back from <c>roomGross</c>), a payment its exact amount without the part that is tourist tax (<see cref="AmountNetOfTouristTax"/>:
/// Octorate's reservation has no tourist tax member to write, so the payments never pass its price).</item>
/// <item><b>Dates.</b> Octorate reads <c>checkin</c> and <c>checkout</c> as instants in UTC: a Rome day is sent as the instant it starts in Rome
/// (<see cref="RomeCalendar.StartOfDayUtc(DateOnly)"/>), in the form <c>2026-12-09T23:00:00+00:00[UTC]</c> of the sandbox and of Octorate's own
/// examples.</item>
/// <item><b>Guest data</b>: the booker's given and family name, and the sex only when CasaZen knows it. Never an e-mail, a phone or a document.</item>
/// </list>
/// </remarks>
internal static class OctorateReservationRequests
{
    /// <summary>Configuration key of the channel of the direct bookings (<c>Octorate__DirectChannelId</c>; 270 in the sandbox). No default in code.</summary>
    public const string DirectChannelIdKey = "Octorate:DirectChannelId";

    /// <summary>Configuration section of the <c>paymentMode</c> of each way CasaZen collects (<c>Octorate__PaymentModes__{Stripe,CashOnArrival,BankTransfer}</c>).</summary>
    public const string PaymentModesSection = "Octorate:PaymentModes";

    /// <summary>The longest <c>refer</c> Octorate takes (OpenAPI: 25).</summary>
    public const int MaxReferenceLength = 25;

    /// <summary>The longest <c>transaction</c> CasaZen sends (the column that keeps it is 100 characters).</summary>
    public const int MaxTransactionLength = Payment.OctorateTransactionIdMaxLength;

    /// <summary>The <c>description</c> of the payments CasaZen records: where they come from, nothing about the guest.</summary>
    public const string PaymentDescription = "CasaZen";

    /// <summary>The longest <c>paymentMode</c> accepted from configuration (Octorate's names are short words such as <c>BANKTRANSFER</c>).</summary>
    private const int MaxPaymentModeLength = 30;

    /// <summary>
    /// The channel of the direct bookings from configuration: a positive whole number; <c>null</c> when the key is missing, empty or not a
    /// number (then nothing is created: CasaZen never picks a channel by itself).
    /// </summary>
    public static int? DirectChannelIdOf(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration[DirectChannelIdKey]?.Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// The <c>paymentMode</c> configured for a way of collecting (<c>Stripe</c>, <c>CashOnArrival</c>, <c>BankTransfer</c>), upper case;
    /// <c>null</c> when it is not configured or is not a word of capital letters and underscores.
    /// </summary>
    public static string? PaymentModeOf(IConfiguration configuration, string collectedBy)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectedBy);
        var text = configuration[$"{PaymentModesSection}:{collectedBy}"]?.Trim().ToUpperInvariant();
        return text is { Length: > 0 and <= MaxPaymentModeLength } && text.All(c => c is (>= 'A' and <= 'Z') or '_') ? text : null;
    }

    /// <summary>The body of <c>POST /rest/v1/reservation/{acc}</c> for <paramref name="request"/> on <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">A request Octorate could not take: no reference, a stay of no night, no guest, a negative price.</exception>
    public static string CreateBody(OctorateBookingTarget target, OctorateReservationCreate request, int channelId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelId);

        var reference = request.ExternalReference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
            throw new ArgumentException($"The reference of a reservation has 1 to {MaxReferenceLength} characters.", nameof(request));
        if (request.CheckOut <= request.CheckIn)
            throw new ArgumentException("A stay has at least one night.", nameof(request));
        if (request.Guests < 1 || request.Children < 0 || request.Children >= request.Guests)
            throw new ArgumentException("A reservation has at least one adult, and the children are counted among the guests.", nameof(request));
        if (request.RoomGross < 0m)
            throw new ArgumentException("The price of a stay is not negative.", nameof(request));

        var booker = new JsonObject
        {
            ["type"] = "BOOKER",
            ["givenName"] = request.BookerFirstName?.Trim() ?? string.Empty,
            ["familyName"] = request.BookerLastName?.Trim() ?? string.Empty,
            ["checkin"] = Day(request.CheckIn),
            ["checkout"] = Day(request.CheckOut),
        };
        if (SexOf(request.BookerGender) is { } sex)
            booker["sex"] = sex;

        var body = new JsonObject
        {
            ["refer"] = reference,
            ["channelId"] = channelId,
            ["status"] = "CONFIRMED",
            ["checkin"] = Instant(RomeCalendar.StartOfDayUtc(request.CheckIn)),
            ["checkout"] = Instant(RomeCalendar.StartOfDayUtc(request.CheckOut)),
            ["createTime"] = Instant(nowUtc),
            ["updateTime"] = Instant(nowUtc),
            ["product"] = IdValue(target.ProductId),
            ["roomGross"] = Money(request.RoomGross),
            ["totalGuest"] = request.Guests,
            ["totalChildren"] = request.Children,
            ["totalInfants"] = 0,
            ["guests"] = new JsonArray(booker),
        };

        // The PMS room of the unit, only when the target knows it: any other pmsProduct answers 500 on the sandbox.
        if (!string.IsNullOrWhiteSpace(target.PmsProductId))
            body["pmsProduct"] = IdValue(target.PmsProductId);

        return body.ToJsonString();
    }

    /// <summary>The longest note CasaZen sends as <c>privateNotes</c> (the notes of a booking are at most 1000 characters).</summary>
    public const int MaxNotesLength = 1000;

    /// <summary>
    /// The body of <c>PATCH /rest/v1/reservation/{acc}/{id}</c> for <paramref name="update"/> on <paramref name="target"/> (E08/F02/US01/T02, NF-75): only
    /// what an edit of a booking changes, the members of the sandbox's partial <c>PATCH</c> (<c>checkin</c>, <c>checkout</c>, <c>totalGuest</c>,
    /// <c>privateNotes</c>) and the price of the stay; the room as <c>pmsProduct</c> when the target has one (a <c>PATCH</c> without it answers 400).
    /// Never a status, a guest, a payment or a field of Octorate's automatic charge (D9). The dates are the instants the Rome days start, as the create sends them.
    /// </summary>
    /// <exception cref="ArgumentException">A change Octorate could not take: no night, no guest, a negative price.</exception>
    public static string UpdateBody(OctorateBookingTarget target, OctorateReservationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(update);

        if (update.CheckOut <= update.CheckIn)
            throw new ArgumentException("A stay has at least one night.", nameof(update));
        if (update.Guests < 1 || update.Children < 0 || update.Children >= update.Guests)
            throw new ArgumentException("A reservation has at least one adult, and the children are counted among the guests.", nameof(update));
        if (update.RoomGross < 0m)
            throw new ArgumentException("The price of a stay is not negative.", nameof(update));

        var body = new JsonObject
        {
            ["checkin"] = Instant(RomeCalendar.StartOfDayUtc(update.CheckIn)),
            ["checkout"] = Instant(RomeCalendar.StartOfDayUtc(update.CheckOut)),
            ["totalGuest"] = update.Guests,
            ["totalChildren"] = update.Children,
            ["roomGross"] = Money(update.RoomGross),
        };

        // The PMS room of the unit, only when the target knows it: any other pmsProduct answers 500 on the sandbox.
        if (!string.IsNullOrWhiteSpace(target.PmsProductId))
            body["pmsProduct"] = IdValue(target.PmsProductId);

        var notes = update.Notes?.Trim();
        if (!string.IsNullOrEmpty(notes))
            body["privateNotes"] = notes.Length > MaxNotesLength ? notes[..MaxNotesLength] : notes;

        return body.ToJsonString();
    }

    /// <summary>The <c>status</c> of a cancelled reservation, as Octorate spells it.</summary>
    public const string CancelledStatus = "CANCELLED";

    /// <summary>
    /// The body of <c>PUT /rest/v1/reservation/{acc}/{id}</c> that cancels a reservation (E08/F02/US02/T02): <paramref name="current"/>, the
    /// reservation as Octorate gave it, word for word, with only its <c>status</c> changed to <c>CANCELLED</c>. A PUT may replace what it does not
    /// carry, so the reservation goes back whole and nothing of it (the dates, the guests, the price, the payments) is rewritten. The room and
    /// the PMS room of <paramref name="target"/> complete a document that left them out (any other <c>pmsProduct</c> answers 500).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="current"/> is not a JSON object.</exception>
    public static string CancelBody(JsonElement current, OctorateBookingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (current.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A reservation is a JSON object.", nameof(current));

        var body = JsonNode.Parse(current.GetRawText())!.AsObject();
        body["status"] = CancelledStatus;
        if (body["product"] is null)
            body["product"] = IdValue(target.ProductId);

        if (!string.IsNullOrWhiteSpace(target.PmsProductId) && body["pmsProduct"] is null)
            body["pmsProduct"] = IdValue(target.PmsProductId);

        return body.ToJsonString();
    }

    /// <summary>The body of <c>POST /rest/v1/reservation/{acc}/{id}/payment</c>: income, never a card charge (D9).</summary>
    /// <exception cref="ArgumentException">No transaction, a mode that is not a word, an amount that is not above zero.</exception>
    public static string PaymentBody(OctoratePaymentRegistration payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var transaction = payment.Transaction?.Trim();
        if (string.IsNullOrEmpty(transaction) || transaction.Length > MaxTransactionLength)
            throw new ArgumentException($"The transaction of a payment has 1 to {MaxTransactionLength} characters.", nameof(payment));

        var mode = payment.PaymentMode?.Trim();
        if (string.IsNullOrEmpty(mode) || mode.Length > MaxPaymentModeLength || !mode.All(c => c is (>= 'A' and <= 'Z') or '_'))
            throw new ArgumentException("The mode of a payment is one of Octorate's words (CASH, BANKTRANSFER, ...).", nameof(payment));

        var amount = Money(payment.Amount);
        if (amount <= 0m)
            throw new ArgumentException("A payment recorded on a reservation is above zero.", nameof(payment));

        var body = new JsonObject
        {
            ["transaction"] = transaction,
            ["paymentMode"] = mode,
            ["amount"] = amount,
            ["referenceTime"] = PaymentInstant(payment.ReceivedAt),
            ["description"] = PaymentDescription,
        };
        return body.ToJsonString();
    }

    /// <summary>
    /// What of a payment goes on the reservation of Octorate, to the cent. The reservation holds the stay (<c>roomGross</c> is the price without the
    /// tourist tax) and Octorate works the tourist tax out itself (<c>touristTax</c> is read-only on its reservations and the create has no such
    /// member), while the guest pays the whole <paramref name="totalPrice"/> to CasaZen. The tax is taken as the last <paramref name="touristTax"/>
    /// of the price: of the payments of the booking, in the order they came in (<paramref name="paidBefore"/> is what came in before this one),
    /// the part that falls in that last slice is left out, so the payments recorded on a reservation never pass its price by the tax. A payment
    /// that pays tourist tax only gives zero; a booking without tourist tax changes nothing.
    /// </summary>
    public static decimal AmountNetOfTouristTax(decimal amount, decimal paidBefore, decimal totalPrice, decimal touristTax)
    {
        var paid = Money(amount);
        var total = Money(totalPrice);
        var tax = Money(Math.Min(touristTax, total));
        if (tax <= 0m)
            return paid;

        var from = Math.Max(0m, Money(paidBefore));
        var to = from + paid;
        var taxStarts = total - tax;
        var taxPaid = Math.Max(0m, Math.Min(to, total) - Math.Max(from, taxStarts));
        return paid - taxPaid;
    }

    /// <summary>An amount to the cent (half away from zero, as every amount of CasaZen).</summary>
    public static decimal Money(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>A UTC instant in the form of the reservations (<c>2026-12-09T23:00:00+00:00[UTC]</c>).</summary>
    public static string Instant(DateTime utc) =>
        AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00[UTC]";

    /// <summary>A UTC instant in the form the payment of the sandbox was accepted with (<c>2026-11-04T20:35:14Z</c>).</summary>
    public static string PaymentInstant(DateTime utc) =>
        AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An id of Octorate as the number the OpenAPI declares; an id that is not a number goes as text (and Octorate refuses it).</summary>
    private static JsonNode IdValue(string id)
    {
        var text = id.Trim();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(text);
    }

    private static string? SexOf(Gender? gender) => gender switch
    {
        Gender.Male => "MALE",
        Gender.Female => "FEMALE",
        _ => null,
    };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
