using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <inheritdoc cref="IIdentityVerificationMetricsService" />
/// <remarks>
/// <para><b>The denominator</b> is every identity step opened in the period (<c>GuestCheckInSession.IdentityOpenedAt</c>): the guest who refused the consent, the document of a
/// format the registry does not hold, a technical error, an unavailable verification, a minor, an abandonment and a verification still in progress are categories of their own, and
/// the ten categories add up to it (<see cref="IdentityOutcomeCategory"/>). A step that is still open is abandoned once it is older than
/// <c>IdentityVerification:Retention:AbandonedAfterHours</c>.</para>
/// <para><b>The rates</b> are the automatic approvals (the outcome of the sessions) and the attempts whose shadow outcome is an approval (the rows of the log), each out of the
/// opened steps, with the Wilson 95% interval of <see cref="IdentityStatistics"/>: the numbers of the criterion «Automazione» are computed by the code of the qualification
/// report, not by a second copy of it. <b>The critical cell</b> of <c>ShadowVsHuman</c> is approve then reject: a would-be false approval.</para>
/// <para><b>The times</b> are nearest-rank percentiles of the same statistics: the presence step of the verifications the policy decided (<c>DurationMs</c> of its row), and
/// the review (from the escalation the policy wrote to the decision of the person on the same booking). A review that wrote no row (a hand-off with no attempt) has no time:
/// <c>N</c> says how many were measured.</para>
/// <para><b>Privacy</b>: the reads are ids, codes, counts and times (the reasons of the decisions are not even read); the service writes nothing and logs nothing.</para>
/// </remarks>
public sealed class IdentityVerificationMetricsService(
    AppDbContext db,
    TimeProvider clock,
    IOptions<IdentityVerificationOptions> options) : IIdentityVerificationMetricsService
{
    public async Task<IdentityVerificationMetrics> GetAsync(HostScope scope, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var records = await IdentityVerificationLedger.ReadAsync(
            db,
            scope,
            fromUtc,
            toUtc,
            clock.GetUtcNow().UtcDateTime,
            IdentityVerificationLedger.AbandonedAfterOf(options.Value),
            withReasons: false,
            cancellationToken);
        return Summarize(records);
    }

    internal static IdentityVerificationMetrics Summarize(IReadOnlyList<IdentityVerificationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var opened = records.Count;
        var outcomes = IdentityOutcomeCounts.From(records.Select(record => record.Outcome));
        var shadowApproved = records.Count(record => record.Decided is { ShadowApproved: true });

        var latencies = records.Select(record => record.Decided?.DurationMs).OfType<int>().Select(value => (double)value).ToList();
        var reviews = records.Select(record => record.Human?.ReviewSeconds).OfType<double>().ToList();

        return new IdentityVerificationMetrics(
            opened,
            outcomes,
            IdentityRate.Of(outcomes.AutoApproved, opened),
            IdentityRate.Of(shadowApproved, opened),
            ShadowVsHuman(records),
            IdentityPercentiles.Of(latencies),
            IdentityPercentiles.Of(reviews),
            Coverage(records));
    }

    private static IdentityShadowVsHuman ShadowVsHuman(IReadOnlyList<IdentityVerificationRecord> records)
    {
        int approveApprove = 0, approveReject = 0, reviewApprove = 0, reviewReject = 0;
        foreach (var record in records)
        {
            if (record.Decided is not { Shadow: not null } decided || record.Human is not { } human)
                continue;

            if (decided.ShadowApproved)
            {
                if (human.Approved)
                    approveApprove++;
                else
                    approveReject++;
            }
            else if (human.Approved)
            {
                reviewApprove++;
            }
            else
            {
                reviewReject++;
            }
        }

        return new IdentityShadowVsHuman(approveApprove, approveReject, reviewApprove, reviewReject);
    }

    private static List<IdentityCoverageCell> Coverage(IReadOnlyList<IdentityVerificationRecord> records)
    {
        return records
            .GroupBy(record => (record.FormatId, record.Channel))
            .OrderBy(group => group.Key.FormatId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Channel, StringComparer.Ordinal)
            .Select(group =>
            {
                var cellOpened = group.Count();
                var autoApproved = group.Count(record => record.Outcome == IdentityOutcomeCategory.AutoApproved);
                var rate = IdentityRate.Of(autoApproved, cellOpened);
                return new IdentityCoverageCell(
                    group.Key.FormatId,
                    group.Key.Channel,
                    cellOpened,
                    autoApproved,
                    group.Count(record => record.Decided is { ShadowApproved: true }),
                    rate.Value ?? 0.0,
                    rate.WilsonLow,
                    rate.WilsonHigh);
            })
            .ToList();
    }
}
