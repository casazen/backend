using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Identity;

// E73/F01/US03/T01 (NF-196, decision D10): the one place that reads the identity steps opened in a period and pairs each with what the policy decided and what a person decided
// after it, so that the counters of the dashboard (IdentityVerificationMetricsService) and the CSV of the pilot (IdentityPilotExport) are made of the same records and cannot
// disagree. Ids, codes, counts and times only: no column read here can hold a name, an image, a value read on a document or a score of a person; the reasons of a decision
// (codes and numbers) are read only when the caller asks for them (the export, which writes their codes and never their numbers).

/// <summary>
/// The row of the log the self-hosted policy wrote when it closed an attempt (a decision of the closure, <see cref="Casazen.Core.Entities.BiometricVerificationLog.DecisionMode"/> not
/// <see cref="IdentityDecisionMode.Legacy"/>): how it was taken, what the policy would have decided, how long the presence step took and which policy decided.
/// </summary>
/// <param name="Id">The id of the row.</param>
/// <param name="Mode">Observation (a person decides) or automatic (the outcome of the policy is the outcome).</param>
/// <param name="Outcome">The outcome of the row: an approval or an escalation to a person (the policy never rejects).</param>
/// <param name="Shadow">What the policy would have decided.</param>
/// <param name="DurationMs">The time of the presence step in milliseconds.</param>
/// <param name="PolicyVersion">The version of the policy file that decided.</param>
/// <param name="PolicyHash">The canonical SHA-256 of that policy file.</param>
/// <param name="ReasonsJson">The reasons of the decision (codes and numbers), only when asked for.</param>
/// <param name="CreatedAtUtc">When the row was written.</param>
internal sealed record IdentityDecidedRow(
    Guid Id,
    IdentityDecisionMode Mode,
    BiometricOutcome Outcome,
    BiometricOutcome? Shadow,
    int? DurationMs,
    string? PolicyVersion,
    string? PolicyHash,
    string? ReasonsJson,
    DateTime CreatedAtUtc)
{
    public bool ShadowApproved => Shadow == BiometricOutcome.Accept;
}

/// <summary>The first decision of a person after a decision of the policy, on the same booking: approved or rejected, when, and (for a review) after how many seconds.</summary>
/// <param name="Approved">The person approved the guest (otherwise rejected).</param>
/// <param name="DecidedAtUtc">When the person decided.</param>
/// <param name="ReviewSeconds">Seconds from the row of the review the policy handed over to this decision; null when the policy decided no review (an approval of the policy).</param>
internal sealed record IdentityHumanDecision(bool Approved, DateTime DecidedAtUtc, double? ReviewSeconds);

internal sealed record IdentityVerificationRecord(
    Guid SessionId,
    Guid OrgId,
    Guid BookingId,
    Guid PropertyId,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    string FormatId,
    string Channel,
    IdentityOutcomeCategory Outcome,
    int GuidedRetries,
    IdentityDecidedRow? Decided,
    IdentityHumanDecision? Human);

internal sealed record IdentitySessionFacts(
    Guid Id,
    Guid OrgId,
    Guid BookingId,
    Guid PropertyId,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    string? OutcomeCode,
    string? FormatId,
    string? Channel,
    int GuidedRetries);

internal sealed record IdentityLogFacts(
    Guid Id,
    Guid BookingId,
    VerificationMethod Method,
    IdentityDecisionMode DecisionMode,
    BiometricOutcome Outcome,
    BiometricOutcome? ShadowOutcome,
    int? DurationMs,
    string? PolicyVersion,
    string? PolicyHash,
    string? ReasonsJson,
    DateTime CreatedAt);

/// <summary>
/// Reads the identity steps opened in <c>[from, to)</c> by the caller's scope, one record per step, with two queries and no query per session.
/// <para><b>Which sessions.</b> The sessions of the org of the scope whose <see cref="Casazen.Core.Entities.GuestCheckInSession.IdentityOpenedAt"/> falls in the period and whose
/// booking the scope reaches (<c>InScope</c>, the rule of the existing metrics): a session of another org, or of a property outside the reach, is never read, whatever the tenant
/// filter of the context says (the org is named in every predicate).</para>
/// <para><b>The category.</b> A closed session (<see cref="Casazen.Core.Entities.GuestCheckInSession.IdentityClosedAt"/>) has the category of its terminal code
/// (<see cref="IdentityOutcomeCategories.OfTerminalCode"/>); an open one is <see cref="IdentityOutcomeCategory.Abandoned"/> when it was opened more than
/// <c>abandonedAfter</c> ago (<c>IdentityVerification:Retention:AbandonedAfterHours</c>), <see cref="IdentityOutcomeCategory.InProgress"/> otherwise. Each opened session has
/// exactly one category.</para>
/// <para><b>The decision of the policy.</b> A session closed by the closure of the presence step (its code is <c>auto_approved</c>, <c>review</c>, <c>technical_error</c> or
/// <c>unavailable</c>, see <see cref="WritesADecisionRow"/>) is the one that wrote a row of the log: the first decision row of its booking (<c>Method = Biometric</c>, a
/// decision mode other than <c>Legacy</c>) written between the opening and the closing of the session; a row belongs to one session only. A session closed with another code (a
/// refusal of the consent, a minor, the assisted path, a format the registry does not hold, an abandonment) wrote none and never looks for one: the retention job closes an
/// abandoned session at the time of the job, so the time between its opening and its closing can hold the row of a later session of the same booking. An open session has none.</para>
/// <para><b>The decision of a person.</b> The first row of an operator with an outcome (<c>Method = Operator</c>, approve or reject: the row that asks the guest for a new
/// photo is not a decision) of the same booking after the decision of the policy and before the next decision of the policy on that booking. The seconds of a review are
/// counted from the row of the review (an escalation) to that row, so a booking handed over twice measures each review once.</para>
/// </summary>
internal static class IdentityVerificationLedger
{
    public static TimeSpan AbandonedAfterOf(IdentityVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var hours = options.Retention.AbandonedAfterHours;
        return TimeSpan.FromHours(hours > 0 ? hours : new IdentityRetentionOptions().AbandonedAfterHours);
    }

    public static async Task<IReadOnlyList<IdentityVerificationRecord>> ReadAsync(
        AppDbContext db,
        HostScope scope,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc,
        TimeSpan abandonedAfter,
        bool withReasons,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        var sessions = await SessionsQuery(db, scope, fromUtc, toUtc).ToListAsync(cancellationToken);
        if (sessions.Count == 0)
            return [];

        var logs = await LogsQuery(db, scope.OrgId, sessions.Select(session => session.BookingId).Distinct().ToArray(), fromUtc, withReasons).ToListAsync(cancellationToken);
        var byBooking = logs
            .GroupBy(row => row.BookingId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToList());

        var records = new List<IdentityVerificationRecord>(sessions.Count);
        foreach (var booking in sessions.GroupBy(session => session.BookingId))
        {
            var rows = byBooking.GetValueOrDefault(booking.Key) ?? [];
            var decisions = rows.Where(row => row.Method == VerificationMethod.Biometric && row.DecisionMode != IdentityDecisionMode.Legacy).ToList();
            var people = rows.Where(row => row.Method == VerificationMethod.Operator && row.Outcome is BiometricOutcome.Accept or BiometricOutcome.Reject).ToList();
            var taken = new HashSet<Guid>();

            foreach (var session in booking.OrderBy(s => s.OpenedAt).ThenBy(s => s.Id))
            {
                var decided = DecisionOf(session, decisions, taken);
                records.Add(RecordOf(session, decided, HumanDecisionOf(decided, decisions, people), nowUtc, abandonedAfter));
            }
        }

        return records.OrderBy(record => record.OpenedAtUtc).ThenBy(record => record.SessionId).ToList();
    }

    internal static IQueryable<IdentitySessionFacts> SessionsQuery(AppDbContext db, HostScope scope, DateTime fromUtc, DateTime toUtc)
    {
        var orgId = scope.OrgId;
        var reachable = db.Bookings.AsNoTracking().InScope(scope);
        return
            from session in db.GuestCheckInSessions.AsNoTracking()
            join booking in reachable on session.BookingId equals booking.Id
            where session.OrgId == orgId
                  && booking.OrgId == orgId
                  && session.IdentityOpenedAt >= fromUtc
                  && session.IdentityOpenedAt < toUtc
            select new IdentitySessionFacts(
                session.Id,
                session.OrgId,
                session.BookingId,
                booking.PropertyId,
                session.IdentityOpenedAt!.Value,
                session.IdentityClosedAt,
                session.IdentityOutcomeCode,
                session.IdentityFormatId,
                session.IdentityChannel,
                session.IdentityGuidedRetries);
    }

    /// <summary>
    /// The second query: the rows of the log of the org on those bookings, from the start of the period on, that can pair with a session: the decisions of the policy and the rows of
    /// an operator. The reasons of a decision carry the numbers it compared (a score of a person): they are selected only when <paramref name="withReasons"/> says so (the export, which
    /// writes their codes and never a number), never for the counters.
    /// </summary>
    internal static IQueryable<IdentityLogFacts> LogsQuery(AppDbContext db, Guid orgId, Guid[] bookingIds, DateTime fromUtc, bool withReasons)
    {
        var rows = db.BiometricVerificationLogs.AsNoTracking()
            .Where(row => row.OrgId == orgId
                          && row.CreatedAt >= fromUtc
                          && bookingIds.Contains(row.BookingId)
                          && (row.Method == VerificationMethod.Operator || row.DecisionMode != IdentityDecisionMode.Legacy));

        return withReasons
            ? rows.Select(row => new IdentityLogFacts(
                row.Id, row.BookingId, row.Method, row.DecisionMode, row.Outcome, row.ShadowOutcome, row.DurationMs, row.PolicyVersion, row.PolicyHash, row.ReasonsJson, row.CreatedAt))
            : rows.Select(row => new IdentityLogFacts(
                row.Id, row.BookingId, row.Method, row.DecisionMode, row.Outcome, row.ShadowOutcome, row.DurationMs, row.PolicyVersion, row.PolicyHash, null, row.CreatedAt));
    }

    /// <summary>
    /// The sessions that wrote a decision row are the ones the closure of the presence step closed (<c>IdentityVerificationFlowService.RecordDecisionAsync</c>, the one writer of the rows of
    /// the policy): their code is <c>auto_approved</c>, <c>review</c>, <c>technical_error</c> or <c>unavailable</c>. A session closed with any other code (the guest asked for a person or
    /// refused the consent, a format the registry does not hold, a minor, an abandonment) wrote none: its time, from the opening to the closing, says nothing about the rows of the
    /// booking, because an abandonment is closed at the time of the retention job, long after the guest left, and a later session of the same booking may have decided meanwhile.
    /// </summary>
    private static bool WritesADecisionRow(string? outcomeCode)
    {
        return outcomeCode is IdentityOutcomeCodes.AutoApproved or IdentityOutcomeCodes.Review or IdentityOutcomeCodes.TechnicalError or IdentityOutcomeCodes.Unavailable;
    }

    private static IdentityLogFacts? DecisionOf(IdentitySessionFacts session, List<IdentityLogFacts> decisions, HashSet<Guid> taken)
    {
        if (session.ClosedAt is not { } closedAt || !WritesADecisionRow(session.OutcomeCode))
            return null;

        var decided = decisions.FirstOrDefault(row => !taken.Contains(row.Id) && row.CreatedAt >= session.OpenedAt && row.CreatedAt <= closedAt);
        if (decided is not null)
            taken.Add(decided.Id);

        return decided;
    }

    /// <summary>
    /// The first decision of a person after <paramref name="decided"/> and before the next decision of the policy on the booking. The rows are in time order, so that next
    /// decision is the first one after it.
    /// </summary>
    private static IdentityHumanDecision? HumanDecisionOf(IdentityLogFacts? decided, List<IdentityLogFacts> decisions, List<IdentityLogFacts> people)
    {
        if (decided is null)
            return null;

        var next = decisions.FirstOrDefault(row => row.CreatedAt > decided.CreatedAt);
        var person = people.FirstOrDefault(row => row.CreatedAt > decided.CreatedAt && (next is null || row.CreatedAt < next.CreatedAt));
        if (person is null)
            return null;

        double? seconds = decided.Outcome == BiometricOutcome.Escalate ? (person.CreatedAt - decided.CreatedAt).TotalSeconds : null;
        return new IdentityHumanDecision(person.Outcome == BiometricOutcome.Accept, person.CreatedAt, seconds);
    }

    private static IdentityVerificationRecord RecordOf(
        IdentitySessionFacts session,
        IdentityLogFacts? decided,
        IdentityHumanDecision? human,
        DateTime nowUtc,
        TimeSpan abandonedAfter)
    {
        IdentityOutcomeCategory category;
        if (session.ClosedAt is not null)
            category = IdentityOutcomeCategories.OfTerminalCode(session.OutcomeCode);
        else
            category = nowUtc - session.OpenedAt > abandonedAfter ? IdentityOutcomeCategory.Abandoned : IdentityOutcomeCategory.InProgress;

        return new IdentityVerificationRecord(
            session.Id,
            session.OrgId,
            session.BookingId,
            session.PropertyId,
            session.OpenedAt,
            session.ClosedAt,
            string.IsNullOrWhiteSpace(session.FormatId) ? IdentityCoverageCell.UnknownFormat : session.FormatId,
            string.IsNullOrWhiteSpace(session.Channel) ? IdentityChannels.Other : session.Channel,
            category,
            session.GuidedRetries,
            decided is null
                ? null
                : new IdentityDecidedRow(
                    decided.Id,
                    decided.DecisionMode,
                    decided.Outcome,
                    decided.ShadowOutcome,
                    decided.DurationMs,
                    decided.PolicyVersion,
                    decided.PolicyHash,
                    decided.ReasonsJson,
                    decided.CreatedAt),
            human);
    }
}
