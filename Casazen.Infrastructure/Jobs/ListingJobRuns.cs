using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// What the three jobs of the status of the listings (E02/F03/US01/T07) have in common: they run over the properties of every org that has something
/// to follow, each property for its own org (the services open <c>IOctorateOrgScope.Use</c>), and one property failing leaves the others alone. An org
/// whose connection is refused, in the wrong environment or over its rate limit stops the org until the next run: its other properties would fail the
/// same way. No retry by Hangfire: the next run reads the same things.
/// </summary>
internal static class ListingJobRuns
{
    /// <summary>Runs <paramref name="action"/> for each (org, property); returns how many ran and how many failed.</summary>
    internal static async Task<(int Done, int Failed)> RunAsync(
        IEnumerable<(Guid OrgId, Guid PropertyId)> targets,
        Func<Guid, Guid, Task> action,
        ILogger logger,
        string jobName,
        CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        foreach (var org in targets.Distinct().GroupBy(target => target.OrgId))
        {
            foreach (var (orgId, propertyId) in org)
            {
                try
                {
                    await action(orgId, propertyId);
                    done++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is OctorateAuthException or OctorateWrongEnvironmentException or OctorateRateLimitException)
                {
                    failed++;
                    logger.LogWarning(
                        "{Job}: org {OrgId} stopped ({Error}), its other properties are left for the next run", jobName, orgId, ex.GetType().Name);
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogWarning(
                        "{Job}: property {PropertyId} of org {OrgId} failed ({Error}), the next run reads it again", jobName, propertyId, orgId, ex.GetType().Name);
                }
            }
        }

        return (done, failed);
    }
}
