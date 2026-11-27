using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>PUT /api/bookings/{id}</c> (PC-07, A2-07): only what the host may change. Status, source, prices, guest,
/// payment and check-in token are never read from the request (unknown JSON properties are ignored): the status changes
/// through its own actions (confirm, check-in, check-out, cancel) and the price is computed by the server. The four amounts of the channel
/// (commission, net, paid, balance) are the one exception: they are read so that a request which tries to change them is refused with 422
/// <c>ota_amounts_readonly</c> instead of being silently ignored (E02/F02/US01/T01, ADR-033-R3).
/// </summary>
public class UpdateBookingRequest : IValidatableObject
{
    [Required]
    public DateTime? CheckInDate { get; set; }

    [Required]
    public DateTime? CheckOutDate { get; set; }

    /// <summary>All guests, minors included.</summary>
    [Range(1, 100)]
    public int NumberOfGuests { get; set; }

    /// <summary>Minors among <see cref="NumberOfGuests"/>.</summary>
    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    /// <summary>Age of each minor at check-in (0-17), asked when the tourist tax of the comune depends on it.</summary>
    public List<int>? ChildrenAges { get; set; }

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    /// <summary>Read-only: the commission of the channel. A value other than the stored one is refused (422 <c>ota_amounts_readonly</c>).</summary>
    public decimal? OtaCommission { get; set; }

    /// <summary>Read-only: the net of the channel. A value other than the stored one is refused (422 <c>ota_amounts_readonly</c>).</summary>
    public decimal? OtaNetAmount { get; set; }

    /// <summary>Read-only: what the guest paid on the channel. A value other than the stored one is refused (422 <c>ota_amounts_readonly</c>).</summary>
    public decimal? OtaPaidAmount { get; set; }

    /// <summary>Read-only: the balance of the channel. A value other than the stored one is refused (422 <c>ota_amounts_readonly</c>).</summary>
    public decimal? OtaBalanceAmount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}

/// <summary>
/// Answer of <c>PUT /api/bookings/{id}</c> (E08/F02/US01/T01, NF-75): the booking as the edit left it, and what happened to the money
/// (<see cref="Settlement"/>). A booking whose price did not change, or that is not one of the booking site, answers the action <c>none</c>.
/// </summary>
public sealed class UpdateBookingResponse : BookingResponseDto
{
    /// <summary>What the edit did with the difference between the new price and what the guest had paid.</summary>
    public BookingEditSettlementDto Settlement { get; set; } = BookingEditSettlementDto.From(BookingEditSettlement.NotNeeded("EUR"));
}

/// <summary>
/// The money of an edit that was saved (<see cref="UpdateBookingResponse.Settlement"/>): <c>action</c> is <c>refund</c>, <c>chargeSavedCard</c>,
/// <c>paymentLink</c> or <c>none</c>; <c>state</c> is <c>done</c> (confirmed by Stripe, or the link sent), <c>pending</c> (Stripe has not confirmed
/// it yet) or <c>none</c>. <c>offlineRefundDue</c> is what the host still has to give back outside CasaZen.
/// </summary>
public sealed class BookingEditSettlementDto
{
    public string Action { get; init; } = "none";

    public string State { get; init; } = "none";

    public decimal Amount { get; init; }

    public decimal OfflineRefundDue { get; init; }

    public string Currency { get; init; } = "EUR";

    public static BookingEditSettlementDto From(BookingEditSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        return new BookingEditSettlementDto
        {
            Action = ActionName(settlement.Action),
            State = settlement.State switch
            {
                BookingSettlementState.Done => "done",
                BookingSettlementState.Pending => "pending",
                _ => "none",
            },
            Amount = settlement.Amount,
            OfflineRefundDue = settlement.OfflineRefundDue,
            Currency = settlement.Currency,
        };
    }

    /// <summary>The name of an action in the API: <c>refund</c>, <c>chargeSavedCard</c>, <c>paymentLink</c>, <c>none</c>.</summary>
    internal static string ActionName(BookingSettlementAction action) => action switch
    {
        BookingSettlementAction.Refund => "refund",
        BookingSettlementAction.ChargeSavedCard => "chargeSavedCard",
        BookingSettlementAction.PaymentLink => "paymentLink",
        _ => "none",
    };
}

/// <summary>
/// Answer of <c>POST /api/bookings/{id}/edit-quote</c> (E08/F02/US01/T01, NF-75): what the edit would do with the money, worked out and not done. The
/// host reads it before confirming. <c>difference</c> is <c>newTotal</c> minus <c>paidSoFar</c>.
/// </summary>
public sealed class BookingEditQuoteDto
{
    public string Currency { get; init; } = "EUR";

    public decimal PreviousTotal { get; init; }

    public decimal NewTotal { get; init; }

    public decimal PaidSoFar { get; init; }

    public decimal Difference { get; init; }

    /// <summary><c>refund</c>, <c>chargeSavedCard</c>, <c>paymentLink</c> or <c>none</c>.</summary>
    public string Action { get; init; } = "none";

    /// <summary>The money that moves online (the refund, the charge or the payment link); zero for <c>none</c>.</summary>
    public decimal Amount { get; init; }

    /// <summary>The part of an overpayment that cannot go back online: the host gives it back.</summary>
    public decimal OfflineRefundDue { get; init; }

    public static BookingEditQuoteDto From(BookingEditQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return new BookingEditQuoteDto
        {
            Currency = quote.Currency,
            PreviousTotal = quote.PreviousTotal,
            NewTotal = quote.NewTotal,
            PaidSoFar = quote.PaidSoFar,
            Difference = quote.Difference,
            Action = BookingEditSettlementDto.ActionName(quote.Action),
            Amount = quote.Amount,
            OfflineRefundDue = quote.OfflineRefundDue,
        };
    }
}

/// <summary>
/// Body of <c>POST /api/bookings/quote</c>: price of a stay the host is entering or changing, with the tourist tax of
/// BK-03 (same answer as the public quote, for any property of the host, listed or not).
/// </summary>
public class HostBookingQuoteRequest : IValidatableObject
{
    [Required]
    public Guid PropertyId { get; set; }

    [Required]
    public DateTime? CheckInDate { get; set; }

    [Required]
    public DateTime? CheckOutDate { get; set; }

    /// <summary>All guests, minors included.</summary>
    [Range(1, 100)]
    public int NumberOfGuests { get; set; }

    /// <summary>Minors among <see cref="NumberOfGuests"/>.</summary>
    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    /// <summary>Age of each minor at check-in (0-17).</summary>
    public List<int>? ChildrenAges { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}

/// <summary>
/// Guests of a host booking: at least one adult (minors fewer than the guests) and, when given, one age of 0-17 per
/// minor (message keys <c>BookingGuestsInvalid</c>, <c>TouristTaxChildAgesInvalid</c>).
/// </summary>
public static class BookingGuestsValidation
{
    public static IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext,
        int numberOfGuests,
        int numberOfChildren,
        IReadOnlyCollection<int>? childrenAges)
    {
        if (numberOfChildren >= numberOfGuests)
        {
            var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
            yield return new ValidationResult(
                localizer?["BookingGuestsInvalid"].Value ?? "BookingGuestsInvalid",
                ["NumberOfChildren"]);
        }

        foreach (var result in ChildrenAgesValidation.Validate(validationContext, childrenAges, numberOfChildren))
            yield return result;
    }
}

/// <summary>
/// Answer of <c>POST /api/bookings/{id}/check-in</c> ("Registra arrivo", CO-08): the booking, now checked in, and whether
/// the guest data of the stay are complete for the Alloggiati communication. Incomplete data never block the arrival: the
/// host completes them afterwards from the Alloggiati tab of the booking.
/// </summary>
public sealed class ArrivalRegisteredResponse : BookingResponseDto
{
    /// <summary>False when the data of at least one guest of the stay are missing for the Alloggiati communication.</summary>
    public bool GuestDataComplete { get; set; }
}

/// <summary>
/// Optional body of <c>POST /api/bookings/{id}/check-out</c> and <c>POST /api/bookings/{id}/checkout-wizard/start</c>
/// (CO-08). No body is the same as <c>registerArrival: false</c>.
/// </summary>
public sealed class StayCheckOutRequest
{
    /// <summary>
    /// The host confirms that the guest arrived: a confirmed booking whose arrival was never registered is checked in
    /// first ("registra arrivo e procedi"). Without it such a booking answers 409 <c>booking_arrival_not_registered</c>.
    /// </summary>
    public bool RegisterArrival { get; set; }
}
