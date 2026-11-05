using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// What the handlers of the booking events (E08/F06/US01/T02) share when they raise a "To do" about a booking: the question "was this
/// item raised for this booking already?" and the one-line reason, which names the fact and nobody (ADR-026-R3).
/// </summary>
internal static class BookingTodoItems
{
    /// <summary>Key of the reason of the cleaning "To do" in <see cref="EmailTexts"/> (Italian and English).</summary>
    public const string CleaningDueReason = "TodoReason_CleaningDue";

    /// <summary>Key of the reason of the special-request "To do" in <see cref="EmailTexts"/> (Italian and English).</summary>
    public const string SpecialRequestReason = "TodoReason_BookingSpecialRequest";

    /// <summary>
    /// An item of <paramref name="type"/> about the booking was raised already, open or closed: one item per booking, whatever the
    /// number of events that ask for it and whatever happened to the item since (a host who closed it by hand does not get it back
    /// because the event is delivered again, ADR-022-R7). <see cref="Casazen.Core.Services.IInternalActivityService.CreateAsync"/>
    /// alone is idempotent for an item that is still open.
    /// </summary>
    public static Task<bool> WasRaisedAsync(AppDbContext db, Guid orgId, ActivityType type, Guid bookingId, CancellationToken cancellationToken)
    {
        var typeName = type.ToString();
        return db.InternalActivities
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.Action != null
                     && a.ActivityType == typeName
                     && a.EntityType == InternalActivity.BookingEntityType
                     && a.EntityId == bookingId,
                cancellationToken);
    }

    /// <summary>
    /// The text of a reason in the language of the org (<c>Org.DefaultLanguage</c>, the language of what the org sends to its team,
    /// Italian when it has none): a fixed sentence about the fact, with no name, contact or text of the guest.
    /// </summary>
    public static async Task<string> ReasonAsync(AppDbContext db, Guid orgId, string key, CancellationToken cancellationToken)
    {
        var language = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(cancellationToken);

        return EmailTexts.Get(key, OrgInvitationEmails.CultureOf(language));
    }
}
