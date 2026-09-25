using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// One CIN alert to deliver to an org (CO-20): its properties that reached a new stage in this run and the deadline
/// status of the day (phase, date, days left).
/// </summary>
public sealed record CinDeadlineAlert(Guid OrgId, IReadOnlyList<Guid> PropertyIds, CinDeadlineStatus Deadline);

/// <summary>Outcome of one run of <see cref="ICinDeadlineAlertService.RunAsync"/>.</summary>
/// <param name="Skipped">Another run was in progress: nothing done.</param>
/// <param name="Stage">Stage due today (<see cref="CinAlertStages"/>), null when no alert is due yet.</param>
/// <param name="PropertiesAlerted">Properties that moved to <paramref name="Stage"/> in this run.</param>
/// <param name="EmailsQueued">Emails queued, one per org.</param>
public sealed record CinDeadlineAlertRunResult(bool Skipped, int? Stage, int PropertiesAlerted, int EmailsQueued);

/// <summary>
/// Daily alert to the hosts of properties without a valid CIN (CO-20, A5-31): before the configured deadline at each
/// threshold of <c>Cin:AlertDaysBefore</c>, on the deadline day, once after it; without a deadline, one reminder of the
/// obligation. Each stage is sent at most once per property (<see cref="CinAlertState"/>). A property suspended by the
/// compliance status (CO-06) gets only the suspension email.
/// </summary>
public interface ICinDeadlineAlertService
{
    /// <summary>
    /// Sends the alerts due today. Only one run at a time: a concurrent run returns
    /// <see cref="CinDeadlineAlertRunResult.Skipped"/>.
    /// </summary>
    Task<CinDeadlineAlertRunResult> RunAsync(CancellationToken cancellationToken = default);
}
