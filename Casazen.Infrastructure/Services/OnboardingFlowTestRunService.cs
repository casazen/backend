using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingFlowTestRunService" />
/// <remarks>
/// <para><b>Read-only by construction.</b> The property and the booking are read with <c>AsNoTracking</c>, nothing here calls
/// <c>SaveChangesAsync</c> or records an event, and every check only reads (<see cref="IFlowDryRunCheck"/>). At the end the unit of work is
/// compared with what it held at the start: a change any check left pending is an <see cref="InvalidOperationException"/>, so a check that
/// starts writing fails the test loudly instead of saving something by the next save of the request.</para>
/// <para><b>Reach.</b> The property is bound in SQL to the org of the scope and to what it reaches; the booking to that property and org.</para>
/// </remarks>
public sealed class OnboardingFlowTestRunService(
    AppDbContext db,
    IEnumerable<IFlowDryRunCheck> checks,
    ILogger<OnboardingFlowTestRunService> logger) : IOnboardingFlowTestRunService
{
    private static readonly OnboardingFlow[] AllFlows = [.. Enum.GetValues<OnboardingFlow>().OrderBy(flow => (int)flow)];

    public async Task<FlowTestRunResult> RunAsync(HostScope scope, Guid propertyId, Guid? bookingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var pendingAtStart = PendingEntities();

        var found = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == propertyId)
            .InScope(scope)
            .AnyAsync(ct);
        if (!found)
        {
            throw new NotFoundException("Property not found for the final test of the flows.")
            {
                Code = OnboardingActivationCodes.PropertyNotFound,
                MessageKey = "PropertyNotFound",
            };
        }

        var imported = db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.PropertyId == propertyId && b.OctorateReservationId != null);
        if (bookingId is { } chosen)
            imported = imported.Where(b => b.Id == chosen);

        var booking = await imported
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("No imported booking for the final test of the flows.")
            {
                Code = OnboardingActivationCodes.TestBookingNotFound,
                MessageKey = "OnboardingTestBookingNotFound",
            };

        var context = new FlowDryRunContext(scope.OrgId, propertyId, booking);
        var byFlow = checks.GroupBy(check => check.Flow).ToDictionary(group => group.Key, group => group.Last());
        var results = new List<FlowCheckResult>(AllFlows.Length);
        foreach (var flow in AllFlows)
        {
            results.Add(byFlow.TryGetValue(flow, out var check)
                ? await check.RunAsync(context, ct)
                : FlowCheckResult.Skipped(flow, FlowCheckReasons.NotProvided));
        }

        if (PendingEntities().Any(entity => !pendingAtStart.Contains(entity)))
            throw new InvalidOperationException("The final test of the flows left changes in the unit of work: a check must only read.");

        logger.LogInformation(
            "Final test of the flows on property {PropertyId}, booking {BookingId}: {Outcomes}",
            propertyId,
            booking.Id,
            string.Join(", ", results.Select(r => $"{r.Flow} {r.Outcome}")));
        return new FlowTestRunResult(propertyId, booking.Id, results);
    }

    /// <summary>The entities the unit of work would write at the next save.</summary>
    private HashSet<object> PendingEntities() =>
        new(
            db.ChangeTracker.Entries()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(entry => entry.Entity),
            ReferenceEqualityComparer.Instance);
}
