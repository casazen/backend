namespace Casazen.Core.Services;

/// <summary>
/// The reminders to the supplier of a request on measure that nobody answered (SP-10c): the second pass of the <c>service-request-expiry</c>
/// job, run after it closed the requests whose deadline had passed. <see cref="Suppliers.ShowcaseReplyReminderRules"/> says which reminder is
/// due; the request keeps when each was queued, so a repeated or retried run sends nothing twice, and the change is saved only if nobody touched
/// the request since it was read (<c>xmin</c>): a supplier that answers at the same moment is not reminded.
/// </summary>
public interface IShowcaseReplyReminderService
{
    /// <summary>One pass. Never throws for a request it cannot handle: it counts it, and the next run goes on.</summary>
    Task<ShowcaseReplyReminderRun> SendDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one pass of <see cref="IShowcaseReplyReminderService"/>.</summary>
/// <param name="Sent">Reminders queued (e-mail and push to the supplier).</param>
/// <param name="Conflicts">Requests that changed under the pass (answered, closed): left as they are, no reminder.</param>
/// <param name="Failed">Requests that could not be handled because of an error: retried by the next run.</param>
public sealed record ShowcaseReplyReminderRun(int Sent, int Conflicts, int Failed)
{
    /// <summary>Nothing was due.</summary>
    public static ShowcaseReplyReminderRun Empty { get; } = new(0, 0, 0);
}
