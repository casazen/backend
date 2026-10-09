using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// One thing that happened, as a service tells the activity log (<see cref="IActivityLog.Record"/>). Ids and codes only: the
/// area and the subject type follow from <see cref="Type"/> (<c>OrgActivityCatalog</c>), and the catalog refuses a detail the
/// type does not allow.
/// </summary>
/// <param name="OrgId">The org the event belongs to: always passed, never read from the request (the acceptance of an invitation writes in the org joined, not in the caller's).</param>
/// <param name="Type">What happened.</param>
/// <param name="ActorUserId">Who did it (<c>User.Id</c>); <c>null</c> when nobody did (a job, a webhook).</param>
/// <param name="SubjectId">The id of the thing it is about, as the type says (a member's account, an invitation, the org, a property). Never a name.</param>
/// <param name="Details">The few codes the type allows (<c>OrgActivityDetailKeys</c>): a role, a tier, a count.</param>
/// <param name="Area">Only when the type does not fix it (the reserved events of the property mode); otherwise the catalog's.</param>
public sealed record OrgActivity(
    Guid OrgId,
    OrgActivityType Type,
    string? ActorUserId,
    string SubjectId,
    IReadOnlyDictionary<string, string>? Details = null,
    OrgActivityArea? Area = null)
{
    /// <summary>An event with its details written as pairs, to keep the call sites to one line.</summary>
    public static OrgActivity Of(
        Guid orgId,
        OrgActivityType type,
        string? actorUserId,
        string subjectId,
        params (string Key, string Value)[] details) =>
        new(orgId, type, actorUserId, subjectId, details.Length == 0 ? null : details.ToDictionary(d => d.Key, d => d.Value));
}

/// <summary>
/// The activity log of an org (AM-02b, wave decision D17): who did what, in a short list with no personal data. A service
/// calls <see cref="Record"/> <b>inside the unit of work of the change it records</b>.
/// </summary>
/// <remarks>
/// <para><b>Same transaction.</b> <see cref="Record"/> does not save: it stages the line in the change tracker of the request,
/// and the service's own <c>SaveChanges</c> writes it together with the change, in the same transaction. A change that is
/// rolled back leaves no line; a line can never be written for a change that did not happen. Call it after the checks and
/// before the save that persists the change, and only when something really changed (an idempotent repeat records nothing).</para>
/// <para><b>Poor on purpose.</b> The event must be one of <see cref="OrgActivityType"/> and its details only the keys that type
/// allows, with short code values: <see cref="Record"/> throws <see cref="ArgumentException"/> otherwise (a programming error, found by
/// the tests of the service that calls it), so a name, an email or a free text cannot get into the log by accident.</para>
/// </remarks>
public interface IActivityLog
{
    /// <summary>Stages the event in the current unit of work; the caller saves.</summary>
    /// <exception cref="ArgumentException">The event, its subject or one of its details breaks the rules of the catalog.</exception>
    void Record(OrgActivity activity);
}
