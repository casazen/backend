using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Seeds, at the start, the Italian short-let withholding rule of the configuration (<c>OwnerAccounting:ItalianShortLetRule</c>, ⚖ I9 and R5) as a rule set of every
/// agency that has owners, <b>not approved</b> (E18/F01/US01/T01): the agency finds the draft among its rule sets and its accountant approves it (pinning the source)
/// or writes another; until then the payouts of its Italian owners decide nothing (the rule in force must be approved). Once per org and version
/// (<see cref="IOwnerFiscalProfileService.EnsureItalianShortLetDraftAsync"/>): an agency that gets its first owners is seeded at the next start. A failure is logged and
/// never stops the API. Logs carry counts and ids, nothing else.
/// </summary>
public class OwnerWithholdingDraftSeedHostedService(
    IServiceProvider serviceProvider,
    ILogger<OwnerWithholdingDraftSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fiscal = scope.ServiceProvider.GetRequiredService<IOwnerFiscalProfileService>();

            // The agencies with owners, whatever the tenant: the start reads every org on purpose.
            var orgs = await db.PropertyOwners.IgnoreQueryFilters().AsNoTracking()
                .Select(o => o.OrgId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var seeded = 0;
            foreach (var orgId in orgs)
            {
                if (await fiscal.EnsureItalianShortLetDraftAsync(orgId, cancellationToken) is not null)
                    seeded++;
            }

            logger.LogInformation("Italian short-let withholding draft on startup: {Seeded} seeded of {Orgs} agencies with owners", seeded, orgs.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Italian short-let withholding draft on startup failed: the rule sets of the agencies are unchanged (docs/runbooks/owner-accounting.md)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
