using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

public enum ShowcaseReplyReminder
{
    None = 0,

    Half = 1,

    Last = 2,
}

/// <summary>
/// The reminders to the supplier of a request on measure that nobody answered (SP-10c, interview of 9 October Q7: «risposte dirette entro
/// massimo un'ora»): one at half of the time it has to answer and one ten minutes before the deadline, each <b>at most once</b> (the request
/// keeps when it was queued: <c>ReplyReminderHalfSentAt</c>, <c>ReplyReminderLastSentAt</c>). Pure functions: the clock and the facts of the
/// request come in, the answer is which reminder is due.
/// </summary>
/// <remarks>
/// The reminders only exist while the supplier has <b>not</b> answered: its first message, an offer, a refusal or a take clear the deadline of
/// the request (or end the request), and a request with no deadline is not reminded. After the deadline nothing is reminded: the
/// <c>service-request-expiry</c> job closes the request (and tells the customer), it does not send the call of a time that has passed.
/// </remarks>
public static class ShowcaseReplyReminderRules
{
    public static readonly TimeSpan LastLead = TimeSpan.FromMinutes(10);

    public static bool Applies(
        ServiceRequestRentalContext context,
        ServiceRequestKind kind,
        ServiceRequestStatus status,
        DateTime? responseDueAt,
        DateTime? quoteSentAt) =>
        context == ServiceRequestRentalContext.Showcase
        && kind == ServiceRequestKind.Custom
        && status == ServiceRequestStatus.Richiesto
        && responseDueAt is not null
        && quoteSentAt is null;

    /// <summary>
    /// The reminder due at <paramref name="nowUtc"/> for a request whose wait began at <paramref name="waitStartUtc"/> and ends at
    /// <paramref name="dueUtc"/>. <b>At most one reminder is due at a time</b>: when the job runs late and both moments have passed, only the last
    /// call is sent (the half one would be old news). A wait of 20 minutes or less has no half reminder (the two would be on top of each other) and
    /// one of 10 minutes or less has none at all.
    /// </summary>
    /// <param name="halfSent">The half reminder was already queued.</param>
    /// <param name="lastSent">The last reminder was already queued (then none is due: the half one cannot come after it).</param>
    public static ShowcaseReplyReminder DueOf(DateTime waitStartUtc, DateTime dueUtc, DateTime nowUtc, bool halfSent, bool lastSent)
    {
        var wait = dueUtc - waitStartUtc;

        if (lastSent || nowUtc >= dueUtc || wait <= LastLead)
            return ShowcaseReplyReminder.None;

        if (nowUtc >= dueUtc - LastLead)
            return ShowcaseReplyReminder.Last;

        if (!halfSent && wait > LastLead + LastLead && nowUtc >= waitStartUtc + (wait / 2))
            return ShowcaseReplyReminder.Half;

        return ShowcaseReplyReminder.None;
    }
}
