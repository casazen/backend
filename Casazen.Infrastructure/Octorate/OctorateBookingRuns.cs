using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the daily import and the reconciliation of the bookings have in common (E02/F02/US01/T01 and T02): which properties they read, how
/// far back each one reads, and how one property failing leaves the others alone. A system read over every org: each property is then read
/// for its own org under <see cref="IOctorateOrgScope.Use"/>. The list of the properties (<see cref="ListMappedPropertiesAsync"/>) is also the
/// one the calendar job of E02/F02/US01/T03 reads.
/// </summary>
internal static class OctorateBookingRuns
{
    /// <summary>
    /// How far before the last complete read a run starts again: the webhooks of Octorate are retried for two days (OCT-09), so a change that
    /// was announced and missed is still in the window. Unchanged reservations cost a line of the list and nothing else.
    /// </summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>
    /// The properties to read: the confirmed mappings, with the room chosen, of the orgs whose connection to this environment is connected.
    /// A listing that is proposed, ambiguous or rejected is never read, nor is one of an org that disconnected.
    /// </summary>
    internal static async Task<IReadOnlyList<RunTarget>> ListTargetsAsync(AppDbContext db, OctorateEnvironment environment, CancellationToken ct)
    {
        var mapped = await ListMappedPropertiesAsync(db, environment, ct);
        return [.. mapped.Select(m => new RunTarget(m.OrgId, m.PropertyId, m.BookingsImportedThrough))];
    }

    /// <summary>
    /// The single definition of "which properties are read from Octorate" (the bookings and the calendar): each one with the accommodation and
    /// the room the mapping confirmed, ordered by org so a caller opens one <see cref="IOctorateOrgScope.Use"/> per org.
    /// </summary>
    internal static async Task<IReadOnlyList<MappedProperty>> ListMappedPropertiesAsync(
        AppDbContext db,
        OctorateEnvironment environment,
        CancellationToken ct)
    {
        var connectionIds = await db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (connectionIds.Count == 0)
            return [];

        var rows = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.Status == OctorateMappingStatus.Confirmed
                        && m.PropertyId != null
                        && m.OctorateProductId != null
                        && connectionIds.Contains(m.ConnectionId))
            .OrderBy(m => m.OrgId)
            .ThenBy(m => m.OctorateAccommodationId)
            .Select(m => new { m.OrgId, m.PropertyId, m.OctorateAccommodationId, m.OctorateProductId, m.BookingsImportedThrough })
            .ToListAsync(ct);

        return [.. rows.Select(r => new MappedProperty(
            r.OrgId, r.PropertyId!.Value, r.OctorateAccommodationId, r.OctorateProductId!, r.BookingsImportedThrough))];
    }

    /// <summary>
    /// Reads one property: the whole history when it was never read completely, otherwise what changed since the last complete read minus the
    /// overlap. A failure is logged (ids and the type of the error) and does not stop the other properties; <c>false</c> when the org cannot
    /// be read at all (its connection is refused, the environment is wrong, its rate limit is spent) and its other properties would fail alike.
    /// </summary>
    internal static async Task<bool> RunOneAsync(
        IBookingImportService import,
        RunTarget target,
        BookingImportOptions? options,
        RunTotals totals,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var result = target.ImportedThrough is { } through
                ? await import.ImportIncrementalAsync(target.PropertyId, through - Overlap, ct, options)
                : await import.ImportAllAsync(target.PropertyId, ct, options);
            totals.Add(result);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OctorateAuthException or OctorateWrongEnvironmentException or OctorateRateLimitException)
        {
            totals.Failed++;
            logger.LogWarning(
                "Octorate booking read of org {OrgId} stopped ({Error}): the other properties of the org are left for the next run",
                target.OrgId,
                ex.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            totals.Failed++;
            logger.LogWarning(
                "Octorate booking read of property {PropertyId} of org {OrgId} failed ({Error}): the next run reads it again",
                target.PropertyId,
                target.OrgId,
                ex.GetType().Name);
            return true;
        }
    }

    /// <summary>A property to read: its org, itself and the instant of the last complete read of its listing.</summary>
    internal sealed record RunTarget(Guid OrgId, Guid PropertyId, DateTime? ImportedThrough);

    /// <summary>A property of a confirmed mapping: its org, itself, the accommodation and the room (product) of the listing, and the last complete read of its bookings.</summary>
    internal sealed record MappedProperty(Guid OrgId, Guid PropertyId, string AccommodationId, string ProductId, DateTime? BookingsImportedThrough);

    /// <summary>The counts of a whole run, for its log line.</summary>
    internal sealed class RunTotals
    {
        public int Properties { get; set; }

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Skipped { get; set; }

        public int Unmapped { get; set; }

        public int Incomplete { get; set; }

        public int Failed { get; set; }

        public void Add(ImportResult result)
        {
            Properties++;
            Created += result.Created;
            Updated += result.Updated;
            Skipped += result.Skipped;
            Unmapped += result.Unmapped.Count;
            Failed += result.Failed;
            if (!result.IsComplete)
                Incomplete++;
        }
    }
}
