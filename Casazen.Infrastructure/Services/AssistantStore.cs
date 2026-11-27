using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the services of the assistant (E05/F05/US01) share when they read and write: the check that a property is the organization's, and one
/// <c>SaveChangesAsync</c>, which is one transaction, for a change and the growth of the version of the instructions that goes with it.
/// </summary>
/// <remarks>
/// The version of the instructions is the concurrency token of the rules row, so the second of two saves that read the same version finds the row changed and is refused
/// with 409 <c>assistant_concurrent_change</c>, <b>with nothing of it written</b>; the same for two first saves of an organization that has no row yet (the unique index on the
/// organization), and for two saves of the same situation at the same moment (the partial unique indexes). The person tries again and reads the state that won.
/// </remarks>
internal static class AssistantStore
{
    /// <summary>The 409 of a change that lost the race with another one.</summary>
    public const string ConcurrentChangeCode = "assistant_concurrent_change";

    /// <summary>The 404 of a property that is not the organization's, is deleted or does not exist.</summary>
    public const string PropertyNotFoundCode = "property_not_found";

    /// <summary>Writes everything the context tracks; the lost race is the conflict above, and the tracker is cleared so the caller reads the state that won.</summary>
    public static async Task SaveAsync(AppDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw Conflict();
        }
        catch (DbUpdateException ex) when (IsLostRace(ex))
        {
            db.ChangeTracker.Clear();
            throw Conflict();
        }
    }

    /// <summary>
    /// 404 <c>property_not_found</c> unless <paramref name="propertyId"/> is a property of <paramref name="orgId"/> that is not deleted: the rows of the assistant are
    /// written under the organization of the caller, so a property of another organization never gets one.
    /// </summary>
    public static async Task EnsurePropertyAsync(AppDbContext db, Guid orgId, Guid propertyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!await db.Properties.AsNoTracking().AnyAsync(property => property.OrgId == orgId && property.Id == propertyId, ct))
            throw PropertyNotFound();
    }

    public static NotFoundException PropertyNotFound() =>
        new("The property was not found") { Code = PropertyNotFoundCode, MessageKey = "PropertyNotFound" };

    private static bool IsLostRace(DbUpdateException ex) =>
        OrgTeamAccess.IsUniqueViolation(ex, OrgAssistantRules.OrgIndexName)
        || OrgTeamAccess.IsUniqueViolation(ex, PropertyAssistantSettings.OrgPropertyIndexName)
        || OrgTeamAccess.IsUniqueViolation(ex, AssistantSituationSetting.OrgSituationIndexName)
        || OrgTeamAccess.IsUniqueViolation(ex, AssistantSituationSetting.PropertySituationIndexName);

    private static DomainConflictException Conflict() => new(ConcurrentChangeCode, "AssistantConcurrentChange");
}
