using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The guests of a stay (<see cref="StayGuest"/>, CO-12): read in record order and replaced as a whole, with the checks
/// of the Alloggiati record (kinds and order of the guests, document only for single guests and heads, shape of codes).
/// Callers scope the booking (guest portal token or host authorization) before calling.
/// </summary>
public interface IStayGuestService
{
    /// <summary>
    /// The guests of the booking by <see cref="StayGuest.Position"/>. When none has been registered yet, the booker as
    /// single guest or head of family (<see cref="StayGuest.FromBooker"/>), not saved.
    /// </summary>
    Task<IReadOnlyList<StayGuest>> GetForBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>The guests of several bookings of the same org, with the same fallback, keyed by booking id.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>> GetForBookingsAsync(
        IReadOnlyCollection<Booking> bookings,
        CancellationToken cancellationToken = default);

    /// <summary>Checks the guests without saving; empty when they can be saved.</summary>
    Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(
        IReadOnlyList<StayGuestInput> guests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and replaces the guests of the booking (the first one stays linked to the booker). Returns the field
    /// errors and saves nothing when the guests are not valid. Does not call <c>SaveChanges</c> on success when
    /// <paramref name="save"/> is false, so a caller can commit it with its own changes.
    /// </summary>
    Task<StayGuestSaveResult> ReplaceAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        bool save = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One guest as entered on the guest portal or by the host. Kind and document type are text so an unknown value gets
/// a field error instead of an unreadable request. Codes are optional: they come from the official tables when
/// imported, otherwise the name alone is stored and the code stays "to complete".
/// </summary>
public sealed class StayGuestInput
{
    public string? Type { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public Gender? Gender { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public bool? BornInItaly { get; init; }
    public string? BirthComuneCode { get; init; }
    public string? BirthComuneName { get; init; }
    public string? BirthProvince { get; init; }
    public string? BirthCountryCode { get; init; }
    public string? BirthCountryName { get; init; }
    public string? CitizenshipCode { get; init; }
    public string? CitizenshipName { get; init; }
    public string? DocumentType { get; init; }
    public string? DocumentTypeCode { get; init; }
    public string? DocumentNumber { get; init; }
    public string? DocumentIssuePlaceCode { get; init; }
    public string? DocumentIssuePlaceName { get; init; }
}

/// <summary>
/// A field error of the guests: <paramref name="Index"/> is the position of the guest (null for an error of the list or
/// of the request, e.g. <c>Guests</c>), <paramref name="Field"/> the <see cref="StayGuestInput"/> property name and <paramref name="MessageKey"/> a
/// SharedResources key formatted with <paramref name="MessageArgs"/>.
/// </summary>
public sealed record StayGuestFieldError(int? Index, string Field, string MessageKey, params object[] MessageArgs)
{
    /// <summary>Model state key of the error in a request whose guest list is named <paramref name="listName"/> (e.g. <c>Guests[1].DocumentNumber</c>).</summary>
    public string ModelStateKey(string listName) => Index is { } index ? $"{listName}[{index}].{Field}" : Field;
}

public sealed record StayGuestSaveResult(IReadOnlyList<StayGuest> Guests, IReadOnlyList<StayGuestFieldError> Errors)
{
    public bool Success => Errors.Count == 0;
}
