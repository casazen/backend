using System.ComponentModel.DataAnnotations;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>PUT /api/bookings/{id}</c> (PC-07, A2-07): only what the host may change. Status, source, prices, guest,
/// payment and check-in token are never read from the request (unknown JSON properties are ignored): the status changes
/// through its own actions (confirm, check-in, check-out, cancel) and the price is computed by the server.
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
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
