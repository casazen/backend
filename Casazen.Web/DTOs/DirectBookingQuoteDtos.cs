using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

public class DirectBookingQuoteRequest : IValidatableObject
{
    [Required]
    public Guid PropertyId { get; set; }

    [Required]
    public DateTime CheckInDate { get; set; }

    [Required]
    public DateTime CheckOutDate { get; set; }

    [Range(1, 100)]
    public int NumberOfAdults { get; set; }

    [Range(0, 100)]
    public int NumberOfChildren { get; set; }

    public List<int>? ChildrenAges { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ChildrenAgesValidation.Validate(validationContext, ChildrenAges, NumberOfChildren);
}

public class DirectBookingQuoteResponse
{
    public IReadOnlyList<DirectCheckoutExtraOffer> ExtraOffers { get; set; } = [];
    public Guid PropertyId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public decimal NightlyRate { get; set; }

    /// <summary>
    /// The price of all the nights: the nightly rate x nights, plus the weekend surcharge on the weekend nights when the
    /// property charges one (DB-03), <b>before</b> the length-of-stay discount. With no surcharge, the default, it is the nightly rate
    /// x nights as it always was.
    /// </summary>
    public decimal LodgingTotal { get; set; }

    public decimal LengthOfStayDiscount { get; set; }

    public decimal? LengthOfStayDiscountPercent { get; set; }

    public int? LengthOfStayDiscountMinNights { get; set; }

    public decimal CleaningFee { get; set; }

    public decimal BasePrice { get; set; }

    public TouristTaxQuoteDto TouristTax { get; set; } = new();

    public decimal TotalPrice { get; set; }

    public string Currency { get; set; } = StayCurrency.Default;

    public DirectBookingPaymentOptionsDto PaymentOptions { get; set; } = new();

    /// <summary>
    /// The price breakdown in cents (DB-03), in order: <c>Nights</c> (the ordinary nights; every night when there is no
    /// weekend surcharge; one more line for each other price that the host confirmed for dates of the stay, so the list may
    /// hold several), <c>WeekendNights</c> (Friday and Saturday nights at the rate with the surcharge, only when there
    /// are such nights), <c>LengthOfStayDiscount</c> (a negative amount, only when a tier of the property applies),
    /// <c>CleaningFee</c> (only when not zero), <c>TouristTax</c> (only when its amount is known: see
    /// <see cref="TouristTax"/> otherwise) and <c>Total</c>. The lines before the total add up to it, to the cent. The
    /// numbers above (<see cref="TotalPrice"/> and the others) are unchanged.
    /// </summary>
    public IReadOnlyList<QuoteLineDto> Lines { get; set; } = [];

    public StayCancellation? Cancellation { get; set; }

    public static DirectBookingQuoteResponse From(DirectBookingQuote quote) => new()
    {
        PropertyId = quote.PropertyId,
        ExtraOffers = quote.ExtraOffers,
        CheckInDate = quote.CheckInDate,
        CheckOutDate = quote.CheckOutDate,
        Nights = quote.Nights,
        NightlyRate = quote.NightlyRate,
        LodgingTotal = quote.Lodging.Total,
        LengthOfStayDiscount = quote.Lodging.Discount?.Amount ?? 0m,
        LengthOfStayDiscountPercent = quote.Lodging.Discount?.PercentOff,
        LengthOfStayDiscountMinNights = quote.Lodging.Discount?.MinNights,
        CleaningFee = quote.CleaningFee,
        BasePrice = quote.BasePrice,
        TouristTax = TouristTaxQuoteDto.From(quote.TouristTax),
        TotalPrice = quote.TotalPrice,
        Currency = quote.Currency,
        PaymentOptions = DirectBookingPaymentOptionsDto.From(quote.PaymentOptions),
        Lines = StayPricing.Lines(quote).Select(QuoteLineDto.From).ToList(),
        Cancellation = quote.Cancellation,
    };
}

public class QuoteLineDto
{
    public QuoteLineKind Kind { get; set; }

    public int? Quantity { get; set; }

    public long? UnitAmountCents { get; set; }

    public long AmountCents { get; set; }

    public static QuoteLineDto From(QuoteLine line) => new()
    {
        Kind = line.Kind,
        Quantity = line.Quantity,
        UnitAmountCents = line.UnitAmountCents,
        AmountCents = line.AmountCents,
    };
}

public class DirectBookingPaymentOptionsDto
{
    public bool DeferredPaymentAvailable { get; set; }

    public DateOnly? DeferredChargeDate { get; set; }

    /// <summary>
    /// Last day the guest can cancel for free by themselves from the guest area (DB-08a): the last day of the full refund of the
    /// stay's cancellation policy. Null when the property has no policy, the policy never refunds in full, or the day is past:
    /// the checkout then promises no free cancellation.
    /// </summary>
    public DateOnly? FreeCancellationUntil { get; set; }

    public static DirectBookingPaymentOptionsDto From(DirectBookingPaymentOptions options) => new()
    {
        DeferredPaymentAvailable = options.DeferredPaymentAvailable,
        DeferredChargeDate = options.DeferredChargeDate is { } charge ? RomeCalendar.DateInRome(charge) : null,
        FreeCancellationUntil = options.FreeCancellationUntil is { } until ? RomeCalendar.DateInRome(until) : null,
    };
}

public class TouristTaxQuoteDto
{
    public TouristTaxQuoteStatus Status { get; set; }

    public decimal? Amount { get; set; }

    public int TaxableNights { get; set; }

    public bool AgeRulesApply { get; set; }

    public IReadOnlyList<string> Categories { get; set; } = [];

    public static TouristTaxQuoteDto From(TouristTaxQuote quote) => new()
    {
        Status = quote.Status,
        Amount = quote.Status == TouristTaxQuoteStatus.Calculated ? quote.Amount : null,
        TaxableNights = quote.TaxableNights,
        AgeRulesApply = quote.AgeRulesApply,
        Categories = quote.Categories,
    };
}

public static class ChildrenAgesValidation
{
    public static IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext,
        IReadOnlyCollection<int>? childrenAges,
        int numberOfChildren)
    {
        if (childrenAges is null)
            yield break;

        if (childrenAges.Count != numberOfChildren
            || childrenAges.Any(age => age is < 0 or >= TouristTaxCalculator.AdultAge))
        {
            var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
            yield return new ValidationResult(
                localizer?["TouristTaxChildAgesInvalid"].Value ?? "TouristTaxChildAgesInvalid",
                ["ChildrenAges"]);
        }
    }
}
