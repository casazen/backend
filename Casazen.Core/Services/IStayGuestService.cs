using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The guests of a stay (<see cref="StayGuest"/>, CO-12): read in record order and replaced as a whole, with the checks
/// of the Alloggiati record (kinds and order of the guests, document only for single guests and heads, shape of codes).
/// Callers scope the booking (guest portal token or host authorization) before calling.
/// </summary>
public interface IStayGuestService
{
    Task<IReadOnlyList<StayGuest>> GetForBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>
    /// The positions of the guests of <paramref name="booking"/> whose <b>full document number</b> the host may read (<c>GET api/alloggiati/{bookingId}/stay-guests/document-numbers</c>): the
    /// single guests and the heads (<see cref="Regulatory.AlloggiatiRecordRules.RequiresDocument"/>, as ever) and, in a stay of a property in canton Ticino (NF-132), every <b>adult</b> family or
    /// group member the police file notifies on their own row (<see cref="Regulatory.TicinoPoliceFile.NotifiedAdultPositions"/>, the very predicate <see cref="ReplaceAsync"/> and the check-in use to
    /// ask and keep the document of every adult the file notifies), because the Ticino check-in stores their numbers. Never a minor family or group member, whatever is stored; any other stay,
    /// Italian or of another canton, answers exactly what it always did. <paramref name="guests"/> are the guests of the stay as <see cref="GetForBookingAsync"/> reads them.
    /// </summary>
    Task<IReadOnlySet<int>> GetDocumentNumberPositionsAsync(
        Booking booking,
        IReadOnlyList<StayGuest> guests,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>> GetForBookingsAsync(
        IReadOnlyCollection<Booking> bookings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the guests without saving, as the guest portal sends them; empty when they can be saved. The tourist tax exemption is not
    /// read: it is the host's decision, checked only when a host replaces the guests (<see cref="ReplaceAsync"/>). Only the Alloggiati record
    /// is applied: <see cref="ValidateAsync(Booking, IReadOnlyList{StayGuestInput}, CancellationToken)"/> adds the rules of the property.
    /// </summary>
    Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(
        IReadOnlyList<StayGuestInput> guests,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and replaces the guests of the booking (the first one stays linked to the booker). Returns the field
    /// errors and saves nothing when the guests are not valid. Every row records <paramref name="author"/> (CO-09 audit).
    /// Does not call <c>SaveChanges</c> on success when <paramref name="save"/> is false, so a caller can commit it with
    /// its own changes.
    /// </summary>
    /// <remarks>
    /// <para>The input is the whole truth (a null clears the field), with one exception: when <paramref name="author"/> is the guest portal,
    /// <see cref="StayGuest.TaxExemptionCode"/> of each row is kept and never read from the input, because the exemption is the host's
    /// decision (NF-46). A host author sets or clears it, and only to an active, non-automatic exemption reason.</para>
    /// <para>The rules of the property apply: in canton Ticino (NF-132) every guest the police file notifies on their own row needs a document type
    /// and number, and keeps them even as a family or group member (elsewhere a member's document is dropped, as the Alloggiati record has none).</para>
    /// <para><paramref name="vehiclePlate"/> is the plate of the stay (<see cref="Booking.VehiclePlate"/>, NF-132): <c>null</c> leaves the stored one
    /// as it is (a client that does not know the field), an empty text clears it, any other text is normalized (upper case, no spaces) and must be
    /// a plate (<see cref="Regulatory.VehiclePlates"/>), otherwise a field error on <c>VehiclePlate</c>.</para>
    /// </remarks>
    Task<StayGuestSaveResult> ReplaceAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        StayGuestAuthor author,
        bool save = true,
        string? vehiclePlate = null,
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

    public DateOnly? DocumentIssuedOn { get; init; }

    public DateOnly? DocumentExpiresOn { get; init; }

    public bool? ResidenceInItaly { get; init; }
    public string? ResidenceComuneCode { get; init; }
    public string? ResidenceComuneName { get; init; }
    public string? ResidenceProvince { get; init; }
    public string? ResidenceCountryCode { get; init; }
    public string? ResidenceCountryName { get; init; }

    public string? ResidenceStreet { get; init; }
    public string? ResidencePostalCode { get; init; }
    public string? ResidenceCityName { get; init; }

    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }

    public string? TaxExemptionCode { get; init; }
}

public sealed record StayGuestFieldError(int? Index, string Field, string MessageKey, params object[] MessageArgs)
{
    public string ModelStateKey(string listName) => Index is { } index ? $"{listName}[{index}].{Field}" : Field;
}

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
