using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The rules of the scheduled change of rental mode (PM-02, decisions D16 and D19; <c>gap/06</c> §4.4 and <c>gap/04</c>
/// §4.2), in one place and without a database: the service reads the stays, the imported blocks and the leases, these
/// functions decide. The creation of a change, the preview and the daily job all use them, so a date the preview calls
/// free is a date the job accepts, as long as nothing new arrives.
/// </summary>
/// <remarks>
/// <para><b>Dates.</b> Every date is a calendar day of Europe/Rome as midnight UTC of that day (the storage convention of
/// stay and contract dates); "today" is <c>TimeProvider.TodayInRome()</c>.</para>
/// <para><b>One rule for both directions.</b> A stay, a block or a lease stands in the way of a day <c>D</c> when its last
/// day (the departure of a stay, the first free day of a block, the <c>EndDate</c> of a lease) is on or after <c>D</c>;
/// the property is free from the day after (<see cref="PropertyModeBlocker.FreeFrom"/>). This is the rule that refuses to
/// delete a property while a stay or a lease is still to come (<c>PropertyRepository.SoftDeleteAsync</c>), made to depend
/// on a day; for the leases it is literally the same definition (<see cref="Casazen.Core.Leases.LeaseOccupancy"/>). To
/// long-term the things in the way are the stays (pending, confirmed, checked in; an abandoned checkout hold is not a stay)
/// and the blocks imported from the portal calendars; to short stays, the leases that are not drafts nor rejected. Drafts
/// are not a matter of date: they have to be deleted first (D16).</para>
/// <para><b>The first day</b> is tomorrow at the earliest: the job applies a change at the first run after midnight of Rome,
/// so "today" is not a day a change can be programmed for.</para>
/// </remarks>
public static class PropertyModeRules
{
    /// <summary>
    /// Furthest day a change can be programmed for, in years from today. A lease runs up to 4+4 years, so the first free day
    /// after one can be eight years away; ten leaves room, and stops a typo (year 2206) from reaching the calendar block.
    /// </summary>
    public const int MaxYearsAhead = 10;

    /// <summary>
    /// Length of the calendar block of a property that goes long-term, in years from the day of the change (the "about two
    /// years" of the task): iCal has no event without an end, and the portals read the export, so the dates are closed for a
    /// long time instead of for ever. The block is not renewed by itself (runbook).
    /// </summary>
    public const int CalendarBlockYears = 2;

    /// <summary>The other mode: the mode is exclusive, so a change always goes to it.</summary>
    public static RentalMode Opposite(RentalMode mode) => mode == RentalMode.Short ? RentalMode.Long : RentalMode.Short;

    /// <summary>The first day a change can be programmed for: tomorrow, Europe/Rome.</summary>
    public static DateTime FirstPossibleDay(DateTime todayInRome) => todayInRome.Date.AddDays(1);

    /// <summary>The last day a change can be programmed for (<see cref="MaxYearsAhead"/> years from today).</summary>
    public static DateTime LastPossibleDay(DateTime todayInRome) => todayInRome.Date.AddYears(MaxYearsAhead);

    /// <summary>The first free day after the calendar block of a change that starts on <paramref name="effectiveDate"/>.</summary>
    public static DateTime CalendarBlockEnd(DateTime effectiveDate) => effectiveDate.Date.AddYears(CalendarBlockYears);

    /// <summary>
    /// The first day a change can start: tomorrow, or the day after the last thing in its way, whichever is later. Drafts
    /// have no free day and do not count: they are deleted, not waited for.
    /// </summary>
    public static DateTime EarliestDate(DateTime todayInRome, IEnumerable<PropertyModeBlocker> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var earliest = FirstPossibleDay(todayInRome);
        foreach (var candidate in candidates)
        {
            if (candidate.FreeFrom is { } freeFrom && freeFrom.Date > earliest)
                earliest = freeFrom.Date;
        }

        return earliest;
    }

    /// <summary>
    /// What stands in the way of a change that starts on <paramref name="date"/>: every candidate that is not free yet on
    /// that day (a draft never is), by start date.
    /// </summary>
    public static IReadOnlyList<PropertyModeBlocker> BlockersFor(IEnumerable<PropertyModeBlocker> candidates, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(c => c.FreeFrom is null || c.FreeFrom.Value.Date > date.Date)
            .OrderBy(c => c.Start)
            .ThenBy(c => c.Kind)
            .ThenBy(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// The answer of the preview and the check of the creation: <paramref name="candidates"/> are the stays and blocks (to
    /// long-term) or the leases (to short stays) that end after today; <paramref name="requested"/> is the day asked for
    /// (none = the first possible day); <paramref name="scheduled"/> the change already waiting, if any. A day before
    /// tomorrow is reported as <see cref="PropertyModeErrorCodes.DateTooEarly"/>; what is in the way is then evaluated for
    /// tomorrow, which is the first day that could be chosen.
    /// </summary>
    public static PropertyModePreview Assess(
        Guid propertyId,
        RentalMode from,
        RentalMode to,
        DateTime todayInRome,
        DateTime? requested,
        IEnumerable<PropertyModeBlocker> candidates,
        PropertyModeChange? scheduled)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var today = todayInRome.Date;
        var all = candidates.ToList();
        var earliest = EarliestDate(today, all);
        var date = (requested ?? earliest).Date;
        var firstDay = FirstPossibleDay(today);

        var issues = new List<string>();
        if (scheduled is { Status: PropertyModeChangeStatus.Scheduled })
            issues.Add(PropertyModeErrorCodes.ChangeExists);

        if (date < firstDay)
            issues.Add(PropertyModeErrorCodes.DateTooEarly);
        else if (date > LastPossibleDay(today))
            issues.Add(PropertyModeErrorCodes.DateTooFar);

        var blockers = BlockersFor(all, date < firstDay ? firstDay : date);
        if (blockers.Any(b => b.Kind is PropertyModeBlockerKind.Stay or PropertyModeBlockerKind.ImportedBlock))
            issues.Add(PropertyModeErrorCodes.BlockedByBookings);
        if (blockers.Any(b => b.Kind == PropertyModeBlockerKind.Lease))
            issues.Add(PropertyModeErrorCodes.BlockedByLease);
        if (blockers.Any(b => b.Kind == PropertyModeBlockerKind.DraftLease))
            issues.Add(PropertyModeErrorCodes.BlockedByDraftLease);

        return new PropertyModePreview(
            propertyId, from, to, today, earliest, date, blockers, issues, scheduled is { Status: PropertyModeChangeStatus.Scheduled } ? scheduled : null);
    }

    /// <summary>
    /// Throws the error of the first issue of <paramref name="preview"/>, if it has any: 409 for a change that already
    /// waits and for the stays, blocks and leases in the way (they are the state of the property), 422 for a day that is
    /// not allowed. The messages of the date errors and of the stays and leases carry the day to choose.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="PropertyModeErrorCodes.DateTooEarly"/>, <see cref="PropertyModeErrorCodes.DateTooFar"/>.</exception>
    /// <exception cref="DomainConflictException">The other issues.</exception>
    public static void EnsureAllowed(PropertyModePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.Issues.Count == 0)
            return;

        DomainException error = preview.Issues[0] switch
        {
            PropertyModeErrorCodes.ChangeExists => new DomainConflictException(
                PropertyModeErrorCodes.ChangeExists, PropertyModeErrorCodes.ChangeExistsMessageKey),
            PropertyModeErrorCodes.DateTooEarly => new DomainRuleException(
                PropertyModeErrorCodes.DateTooEarly, PropertyModeErrorCodes.DateTooEarlyMessageKey, FirstPossibleDay(preview.Today)),
            PropertyModeErrorCodes.DateTooFar => new DomainRuleException(
                PropertyModeErrorCodes.DateTooFar, PropertyModeErrorCodes.DateTooFarMessageKey, LastPossibleDay(preview.Today)),
            PropertyModeErrorCodes.BlockedByBookings => new DomainConflictException(
                PropertyModeErrorCodes.BlockedByBookings, PropertyModeErrorCodes.BlockedByBookingsMessageKey, preview.EarliestDate),
            PropertyModeErrorCodes.BlockedByLease => new DomainConflictException(
                PropertyModeErrorCodes.BlockedByLease, PropertyModeErrorCodes.BlockedByLeaseMessageKey, preview.EarliestDate),
            PropertyModeErrorCodes.BlockedByDraftLease => new DomainConflictException(
                PropertyModeErrorCodes.BlockedByDraftLease, PropertyModeErrorCodes.BlockedByDraftLeaseMessageKey),
            var other => throw new InvalidOperationException($"Unknown property mode issue '{other}'."),
        };
        throw error;
    }

    /// <summary>
    /// The <see cref="PropertyModeChange.FailureReason"/> of a change the job found blocked: the code of the first rule that
    /// stands in the way (stays and blocks, then leases, then drafts); <c>null</c> when nothing does.
    /// </summary>
    public static string? FailureReasonOf(IReadOnlyCollection<PropertyModeBlocker> blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);

        if (blockers.Any(b => b.Kind is PropertyModeBlockerKind.Stay or PropertyModeBlockerKind.ImportedBlock))
            return PropertyModeErrorCodes.BlockedByBookings;
        if (blockers.Any(b => b.Kind == PropertyModeBlockerKind.Lease))
            return PropertyModeErrorCodes.BlockedByLease;
        if (blockers.Any(b => b.Kind == PropertyModeBlockerKind.DraftLease))
            return PropertyModeErrorCodes.BlockedByDraftLease;

        return null;
    }
}
