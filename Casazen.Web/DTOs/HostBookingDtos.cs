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

    [Range(1, 100)]
    public int NumberOfGuests { get; set; }

    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    public List<int>? ChildrenAges { get; set; }

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    public decimal? OtaCommission { get; set; }

    public decimal? OtaNetAmount { get; set; }

    public decimal? OtaPaidAmount { get; set; }

    public decimal? OtaBalanceAmount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}

public sealed class UpdateBookingResponse : BookingResponseDto
{
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

    internal static string ActionName(BookingSettlementAction action) => action switch
    {
        BookingSettlementAction.Refund => "refund",
        BookingSettlementAction.ChargeSavedCard => "chargeSavedCard",
        BookingSettlementAction.PaymentLink => "paymentLink",
        _ => "none",
    };
}

public sealed class BookingEditQuoteDto
{
    public string Currency { get; init; } = "EUR";

    public decimal PreviousTotal { get; init; }

    public decimal NewTotal { get; init; }

    public decimal PaidSoFar { get; init; }

    public decimal Difference { get; init; }

    public string Action { get; init; } = "none";

    public decimal Amount { get; init; }

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

public class HostBookingQuoteRequest : IValidatableObject
{
    [Required]
    public Guid PropertyId { get; set; }

    [Required]
    public DateTime? CheckInDate { get; set; }

    [Required]
    public DateTime? CheckOutDate { get; set; }

    [Range(1, 100)]
    public int NumberOfGuests { get; set; }

    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    public List<int>? ChildrenAges { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}

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

public sealed class StayCheckOutRequest
{
    public bool RegisterArrival { get; set; }
}
