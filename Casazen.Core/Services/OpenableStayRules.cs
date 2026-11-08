using System.Linq.Expressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Which stays a short-rent alert may point at. Opening the booking answers 404 when the property
/// is gone: a soft-deleted property is hidden from authorization. The guest sheet and the Alloggiati
/// section have nothing to open when the guest record is gone or was deleted. An alert for either
/// is a link the host cannot follow, so the cockpit, the Alloggiati list and the To do leave it out.
/// A stay whose guest data is merely unfinished stays listed: that page opens and shows what is missing.
/// </summary>
public static class OpenableStayRules
{
    /// <summary>
    /// The property is still there (the soft-delete filter hides one that is not) and the guest record
    /// is still there and was not deleted.
    /// </summary>
    public static Expression<Func<Booking, bool>> CanOpen { get; } =
        booking => booking.Property != null && booking.Guest != null && !booking.Guest.IsDeleted;
}
