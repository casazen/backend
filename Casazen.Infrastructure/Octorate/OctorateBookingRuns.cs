using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

internal static class OctorateBookingRuns
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    internal static async Task<IReadOnlyList<RunTarget>> ListTargetsAsync(
        AppDbContext db, OctorateEnvironment environment, CancellationToken ct, Guid? orgId = null, Guid? propertyId = null)
    {
        var mapped = await ListMappedPropertiesAsync(db, environment, ct, orgId, propertyId);
        return [.. mapped.Select(m => new RunTarget(m.OrgId, m.PropertyId, m.BookingsImportedThrough))];
    }

    internal static async Task<IReadOnlyList<MappedProperty>> ListMappedPropertiesAsync(
        AppDbContext db,
        OctorateEnvironment environment,
        CancellationToken ct,
        Guid? orgId = null,
        Guid? propertyId = null)
    {
        var connectionIds = await db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.Environment == environment && c.Status == OctorateConnectionStatus.Connected
                        && (orgId == null || c.OrgId == orgId))
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (connectionIds.Count == 0)
            return [];

        var rows = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => (orgId == null || m.OrgId == orgId)
                        && (propertyId == null || m.PropertyId == propertyId)
                        && m.Status == OctorateMappingStatus.Confirmed
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

    internal sealed record RunTarget(Guid OrgId, Guid PropertyId, DateTime? ImportedThrough);

    internal sealed record MappedProperty(Guid OrgId, Guid PropertyId, string AccommodationId, string ProductId, DateTime? BookingsImportedThrough);

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
