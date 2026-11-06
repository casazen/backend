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

    /// <summary>
    /// Checks the guests without saving, as the guest portal sends them; empty when they can be saved. The tourist tax exemption is not
    /// read: it is the host's decision, checked only when a host replaces the guests (<see cref="ReplaceAsync"/>).
    /// </summary>
    Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(
        IReadOnlyList<StayGuestInput> guests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and replaces the guests of the booking (the first one stays linked to the booker). Returns the field
    /// errors and saves nothing when the guests are not valid. Every row records <paramref name="author"/> (CO-09 audit).
    /// Does not call <c>SaveChanges</c> on success when <paramref name="save"/> is false, so a caller can commit it with
    /// its own changes.
    /// </summary>
    /// <remarks>
    /// The input is the whole truth (a null clears the field), with one exception: when <paramref name="author"/> is the guest portal,
    /// <see cref="StayGuest.TaxExemptionCode"/> of each row is kept and never read from the input, because the exemption is the host's
    /// decision (NF-46). A host author sets or clears it, and only to an active, non-automatic exemption reason.
    /// </remarks>
    Task<StayGuestSaveResult> ReplaceAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        StayGuestAuthor author,
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

    /// <summary>Day the document was issued: optional, not after today (Europe/Rome) and not before 1900.</summary>
    public DateOnly? DocumentIssuedOn { get; init; }

    /// <summary>Day the document expires: optional, not before the issue day. An expired document is accepted.</summary>
    public DateOnly? DocumentExpiresOn { get; init; }

    /// <summary>Residence in Italy (comune and province) or abroad (state): optional, same shape rules as the birth place.</summary>
    public bool? ResidenceInItaly { get; init; }
    public string? ResidenceComuneCode { get; init; }
    public string? ResidenceComuneName { get; init; }
    public string? ResidenceProvince { get; init; }
    public string? ResidenceCountryCode { get; init; }
    public string? ResidenceCountryName { get; init; }

    /// <summary>Statistical fields (NF-40): optional, a code of the closed lists of <c>IstatGuestFieldLists</c>.</summary>
    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }

    /// <summary>
    /// The tourist tax exemption the host chose (an active, non-automatic reason code). Read only when a host replaces the guests; the
    /// guest portal never changes it.
    /// </summary>
    public string? TaxExemptionCode { get; init; }
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

/// <summary>Who enters the guests of a stay (CO-09): the guest on the portal or a host (user id kept for the audit).</summary>
public sealed record StayGuestAuthor(StayGuestDataSource Source, string? UserId)
{
    public static readonly StayGuestAuthor GuestPortal = new(StayGuestDataSource.GuestPortal, null);

    public static StayGuestAuthor Host(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new StayGuestAuthor(StayGuestDataSource.Host, userId);
    }
}

public sealed record StayGuestSaveResult(IReadOnlyList<StayGuest> Guests, IReadOnlyList<StayGuestFieldError> Errors)
{
    public bool Success => Errors.Count == 0;
}
