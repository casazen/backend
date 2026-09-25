using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>Body of <c>POST /api/public/bookings/quote</c>: the stay to price before booking (BK-03).</summary>
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

    /// <summary>Age of each minor at check-in (0-17), one per child; asked when the tourist tax depends on it.</summary>
    public List<int>? ChildrenAges { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ChildrenAgesValidation.Validate(validationContext, ChildrenAges, NumberOfChildren);
}

/// <summary>Price of the stay: the same numbers <c>POST /api/public/bookings</c> records and charges.</summary>
public class DirectBookingQuoteResponse
{
    public Guid PropertyId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public decimal NightlyRate { get; set; }

    /// <summary>Nightly rate x nights.</summary>
    public decimal LodgingTotal { get; set; }

    public decimal CleaningFee { get; set; }

    /// <summary>Lodging + cleaning.</summary>
    public decimal BasePrice { get; set; }

    public TouristTaxQuoteDto TouristTax { get; set; } = new();

    /// <summary>Base price + tourist tax when calculated (the tax is charged with the rest of the total).</summary>
    public decimal TotalPrice { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>What the checkout may offer and promise for this stay (A3-16).</summary>
    public DirectBookingPaymentOptionsDto PaymentOptions { get; set; } = new();

    public static DirectBookingQuoteResponse From(DirectBookingQuote quote) => new()
    {
        PropertyId = quote.PropertyId,
        CheckInDate = quote.CheckInDate,
        CheckOutDate = quote.CheckOutDate,
        Nights = quote.Nights,
        NightlyRate = quote.NightlyRate,
        LodgingTotal = quote.NightlyRate * quote.Nights,
        CleaningFee = quote.CleaningFee,
        BasePrice = quote.BasePrice,
        TouristTax = TouristTaxQuoteDto.From(quote.TouristTax),
        TotalPrice = quote.TotalPrice,
        Currency = quote.Currency,
        PaymentOptions = DirectBookingPaymentOptionsDto.From(quote.PaymentOptions),
    };
}

/// <summary>Payment options of a stay (BK-07, A3-16): the checkout shows only what applies.</summary>
public class DirectBookingPaymentOptionsDto
{
    /// <summary>"Paga alla scadenza" can be chosen: the charge day is after today in Europe/Rome.</summary>
    public bool DeferredPaymentAvailable { get; set; }

    /// <summary>Day the saved card is charged (Europe/Rome date); null when the deferred payment is not available.</summary>
    public DateOnly? DeferredChargeDate { get; set; }

    /// <summary>
    /// Last day the guest can cancel for free by themselves. Always null today: the guest has no self-service
    /// cancellation (only the host cancels, BK-02), so the checkout must not promise one.
    /// </summary>
    public DateOnly? FreeCancellationUntil { get; set; }

    public static DirectBookingPaymentOptionsDto From(DirectBookingPaymentOptions options) => new()
    {
        DeferredPaymentAvailable = options.DeferredPaymentAvailable,
        DeferredChargeDate = options.DeferredChargeDate is { } charge ? RomeCalendar.DateInRome(charge) : null,
        FreeCancellationUntil = options.FreeCancellationUntil is { } until ? RomeCalendar.DateInRome(until) : null,
    };
}

/// <summary>Tourist tax part of a quote.</summary>
public class TouristTaxQuoteDto
{
    /// <summary>
    /// <c>Calculated</c> (amount known), <c>RateUnavailable</c> / <c>CategoryRequired</c> (CasaZen cannot compute it:
    /// not included in the total), <c>ChildAgesRequired</c> (ask the ages of the minors).
    /// </summary>
    public TouristTaxQuoteStatus Status { get; set; }

    /// <summary>Tax of the stay in euros, only when <see cref="Status"/> is <c>Calculated</c>.</summary>
    public decimal? Amount { get; set; }

    public int TaxableNights { get; set; }

    /// <summary>True when the amount depends on the age of the minors.</summary>
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

/// <summary>Ages of the minors of a stay: one per child, each 0-17 (message key <c>TouristTaxChildAgesInvalid</c>).</summary>
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
