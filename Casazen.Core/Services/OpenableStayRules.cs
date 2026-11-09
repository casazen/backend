using System.Linq.Expressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Which stays and properties a short-rent alert may point at.
/// </summary>
/// <remarks>
/// Opening the booking answers 404 when the property is gone, and the guest sheet has nothing to open when the guest
/// was deleted. A blank name leaves the booking page and the Alloggiati card with nothing to show. A stay that has
/// already ended and still lacks a required guest field cannot be completed on that screen, so it is not listed. A
/// current stay (it has not checked out) stays listed even when fields are missing: that screen is where the host
/// finishes them. A stay whose guest data is already complete stays listed after checkout, so the host can open it
/// and send Alloggiati. A property with no name, address, city or CIN is not listed: the activation screen cannot
/// present the house.
/// </remarks>
public static class OpenableStayRules
{
    /// <summary>
    /// The property is still there (the soft-delete filter hides one that is not) and the guest record
    /// is still there and was not deleted.
    /// </summary>
    public static Expression<Func<Booking, bool>> CanOpen { get; } =
        booking => booking.Property != null && booking.Guest != null && !booking.Guest.IsDeleted;

    /// <summary>The booking page and the Alloggiati card can name the house and the guest.</summary>
    public static Expression<Func<Booking, bool>> CanShow { get; } =
        booking => booking.Property != null && booking.Guest != null && !booking.Guest.IsDeleted
            && booking.Property.Name != ""
            && booking.Guest.FirstName != ""
            && booking.Guest.LastName != "";

    /// <summary>
    /// The Alloggiati screen can show this stay and the host can still finish what it asks.
    /// </summary>
    public static bool CanCompleteStay(
        string? propertyName,
        string? guestFirstName,
        string? guestLastName,
        bool guestDataComplete,
        DateTime checkOutDate,
        DateTime today) =>
        !string.IsNullOrWhiteSpace(propertyName)
        && !string.IsNullOrWhiteSpace(guestFirstName)
        && !string.IsNullOrWhiteSpace(guestLastName)
        && (guestDataComplete || checkOutDate.Date >= today.Date);

    /// <summary>The activation screen can present the house, including its CIN.</summary>
    public static bool CanListProperty(string? name, string? address, string? city, string? cinCode) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(address)
        && !string.IsNullOrWhiteSpace(city)
        && !string.IsNullOrWhiteSpace(cinCode);
}
