using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Whether the identity verification of a <b>booking</b> is with an operator, whatever check-in session the guest is on (E03/F04/US02, review fix).
/// The state of a verification lives on the session, but a session is replaced by a new link (the host sends one, the daily job sends one, the
/// operator asks for a new photo): the decision of a person must not be lost with it. A booking is with an operator when
/// <list type="bullet">
/// <item>its latest log row is the <b>rejection</b> of an operator (<see cref="VerificationMethod.Operator"/>, <see cref="BiometricOutcome.Reject"/>):
/// a new session answers «operator» and nothing is sent again, until the operator opens the way again (a new photo asked from the queue writes an
/// operator row that is not a decision, <see cref="BiometricOutcome.Escalate"/>, which is the latest row from then on);</item>
/// <item>or a session of it handed the verification to an operator (<c>OperatorRequestedAt</c>, not accepted) and no operator row (a decision, or
/// the new photo above) was written since: the hand-off is still open, replaced or expired link or not.</item>
/// </list>
/// The jobs that send a check-in link skip such a booking; the flow answers «operator» in any session of it. Runs in the anonymous guest request,
/// in the jobs and in the operator's request: the query filters of the context apply where there is a tenant and the ids come from rows already
/// bound to their org.
/// </summary>
public static class VerificationHandOff
{
    /// <summary>The bookings among <paramref name="bookingIds"/> that are with an operator: rejected by one, or with a hand-off still open.</summary>
    public static async Task<HashSet<Guid>> BookingsWithOperatorAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(bookingIds);

        if (bookingIds.Count == 0)
            return [];

        // Rejected by an operator: the latest row of the booking is its rejection.
        var rejected = await db.BiometricVerificationLogs
            .AsNoTracking()
            .Where(l => bookingIds.Contains(l.BookingId)
                        && l.Method == VerificationMethod.Operator
                        && l.Outcome == BiometricOutcome.Reject
                        && !db.BiometricVerificationLogs.Any(newer => newer.BookingId == l.BookingId && newer.CreatedAt > l.CreatedAt))
            .Select(l => l.BookingId)
            .ToListAsync(ct);

        var open = await OpenHandOffsAsync(db, bookingIds, ct);
        return [.. rejected, .. open];
    }

    /// <summary>
    /// The bookings among <paramref name="bookingIds"/> with a hand-off still open: a session asked for a person (or a consent was refused, a review,
    /// an error), the verification was not accepted, and no operator row has been written since (a decision, or a new photo asked by the operator).
    /// </summary>
    public static async Task<HashSet<Guid>> OpenHandOffsAsync(AppDbContext db, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(bookingIds);

        if (bookingIds.Count == 0)
            return [];

        var open = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => bookingIds.Contains(s.BookingId)
                        && s.OperatorRequestedAt != null
                        && s.BiometricOutcome != BiometricOutcome.Accept
                        && !db.BiometricVerificationLogs.Any(l =>
                            l.BookingId == s.BookingId
                            && l.Method == VerificationMethod.Operator
                            && l.CreatedAt >= s.OperatorRequestedAt))
            .Select(s => s.BookingId)
            .ToListAsync(ct);

        return [.. open];
    }

    /// <summary>Whether the verification of <paramref name="bookingId"/> is with an operator.</summary>
    public static async Task<bool> IsWithOperatorAsync(AppDbContext db, Guid bookingId, CancellationToken ct) =>
        (await BookingsWithOperatorAsync(db, [bookingId], ct)).Count > 0;

    /// <summary>Whether <paramref name="bookingId"/> has a hand-off still open: the operators were told and nobody decided yet.</summary>
    public static async Task<bool> HasOpenHandOffAsync(AppDbContext db, Guid bookingId, CancellationToken ct) =>
        (await OpenHandOffsAsync(db, [bookingId], ct)).Count > 0;
}
