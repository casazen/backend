using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Deadline of the RLI registration of a long-term lease (LT-04, A7-04). Single source of the rule, verified by RS-5
/// (<c>.claude/context/regulations/fiscale.md</c>, rule L1, Agenzia delle Entrate): the contract is registered
/// "entro 30 giorni dalla data di stipula o dalla data di decorrenza, se anteriore", so
/// <c>deadline = min(stipula, decorrenza) + 30 giorni</c>.
/// </summary>
/// <remarks>
/// <para>Calendar days on the Europe/Rome calendar. Dates are stored as midnight UTC of the calendar date (FD-06); the
/// stipula is the Rome day on which the last party signed.</para>
/// <para>The deadline is the base date. The verified sources do not say whether a deadline falling on a Saturday or a
/// public holiday moves to the next working day, so no shift is applied (open point in <c>docs/runbooks/rli.md</c>).
/// </para>
/// </remarks>
public static class RliRegistrationDeadline
{
    /// <summary>Days from the stipula, or from the start date if earlier (rule L1).</summary>
    public const int DaysFromStipulaOrStart = 30;

    /// <summary>
    /// <c>min(stipula, start) + 30</c> days, as midnight UTC of the Europe/Rome calendar date. Both values may be
    /// date-only values (midnight UTC) or instants: each is taken as its Europe/Rome calendar date.
    /// </summary>
    public static DateTime Compute(DateTime stipulaDate, DateTime startDate)
    {
        var stipula = RomeCalendar.DateInRome(stipulaDate);
        var start = RomeCalendar.DateInRome(startDate);
        var from = stipula < start ? stipula : start;
        return ToStoredDate(from.AddDays(DaysFromStipulaOrStart));
    }

    /// <summary>The Europe/Rome calendar date of <paramref name="value"/>, as midnight UTC (storage convention).</summary>
    public static DateTime ToStoredDate(DateTime value) => ToStoredDate(RomeCalendar.DateInRome(value));

    /// <summary>
    /// The deadline of a lease as far as it is known on <paramref name="todayInRome"/>, or <c>null</c> while it is to be
    /// determined:
    /// <list type="bullet">
    /// <item>stipula recorded: <see cref="Compute"/>;</item>
    /// <item>contract not signed by every party yet (Draft, AwaitingSignature, PartiallySigned): the stipula can only
    /// happen today or later, so once the start date is reached (start ≤ today) the start date is the earlier of the two
    /// and the deadline is <c>start + 30</c>; before that it depends on the signing day (to be determined, and at
    /// least 30 days away);</item>
    /// <item>signed but no stipula date recorded (older leases without a signing event): to be determined, never
    /// guessed.</item>
    /// </list>
    /// </summary>
    public static DateTime? Resolve(LeaseStatus status, DateTime? stipulaDate, DateTime startDate, DateTime todayInRome)
    {
        if (stipulaDate is { } stipula)
            return Compute(stipula, startDate);

        if (!IsBeforeFullSignature(status))
            return null;

        var start = RomeCalendar.DateInRome(startDate);
        return start <= RomeCalendar.DateInRome(todayInRome)
            ? ToStoredDate(start.AddDays(DaysFromStipulaOrStart))
            : null;
    }

    /// <inheritdoc cref="Resolve(LeaseStatus, DateTime?, DateTime, DateTime)"/>
    public static DateTime? Resolve(LeaseContract lease, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Resolve(lease.Status, lease.StipulaDate, lease.StartDate, todayInRome);
    }

    /// <summary>Calendar days from <paramref name="todayInRome"/> to <paramref name="deadline"/>: 0 on the deadline day, negative once it has passed.</summary>
    public static int DaysRemaining(DateTime deadline, DateTime todayInRome) =>
        RomeCalendar.DateInRome(deadline).DayNumber - RomeCalendar.DateInRome(todayInRome).DayNumber;

    /// <summary>The contract is not signed by every party yet: no stipula has happened in CasaZen.</summary>
    public static bool IsBeforeFullSignature(LeaseStatus status) =>
        status is LeaseStatus.Draft or LeaseStatus.AwaitingSignature or LeaseStatus.PartiallySigned;

    /// <summary>
    /// The lease still has to be registered: every status before <see cref="LeaseStatus.Registered"/>, signed or not.
    /// <see cref="LeaseStatus.Rejected"/> (a contract that will not be registered) is excluded.
    /// </summary>
    public static bool AwaitsRegistration(LeaseStatus status) =>
        status is not (LeaseStatus.Registered or LeaseStatus.Rejected);

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
