using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The plan of the activation of CasaZen with Octorate (NF-104, NF-10, ONB-01, UX-10, E10/F01/US01/T03): the eleven parts of
/// <see cref="OnboardingPartCatalog"/> with the state of each, derived on every read from the stored facts, the connected path of UX-10, and the
/// host's «Do it later» (defer) and «Save and exit» (exit) of a part, each with a "To do". The plan orchestrates the wizards of the other epics:
/// it reads what they stored, it never does their work.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> The reads count only the properties the scope reaches (AM-03): a collaborator «Solo alcuni» sees the plan over the
/// properties it was given. The mutations are org-level and the web layer lets only an org administrator call them.</para>
/// <para><b>The "To do".</b> A deferral or an exit raises one "To do" through <see cref="IInternalActivityService"/>
/// (<see cref="ActivityType.WizardResume"/>, <see cref="ActivityAction.ResumeWizard"/>, entity <see cref="OnboardingPlanCodes.TodoEntityType"/>,
/// the id of the stored <c>OnboardingPartState</c>, no property): codes and ids only, never a name or a person (ADR-026-R3). A resume closes
/// it.</para>
/// </remarks>
public interface IOnboardingPlanService
{
    /// <summary>The plan of <paramref name="orgId"/> over what <paramref name="scope"/> reaches.</summary>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not a scope of <paramref name="orgId"/>.</exception>
    Task<OnboardingPlan> GetPlanAsync(Guid orgId, HostScope scope, CancellationToken ct);

    /// <summary>The connected path of UX-10 of <paramref name="orgId"/> over what <paramref name="scope"/> reaches.</summary>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not a scope of <paramref name="orgId"/>.</exception>
    Task<OnboardingPath> GetPathAsync(Guid orgId, HostScope scope, CancellationToken ct);

    /// <summary>
    /// «Do it later»: stores the part as deferred and raises its "To do", due at <paramref name="remindAt"/> (UTC) or, without one, tomorrow at
    /// 09:00 in Rome. 422 <see cref="OnboardingPlanCodes.PartPrerequisite"/> for a part another part requires, 422
    /// <see cref="OnboardingPlanCodes.RemindAtInvalid"/> for a reminder that is not in the future or is more than
    /// <see cref="OnboardingPlanLimits.MaxRemindDays"/> days away, 422 <see cref="OnboardingPlanCodes.PartNotOpen"/> for a part that is done or
    /// not available (nothing to put off: no "To do" is raised for it).
    /// </summary>
    Task<OnboardingPartChange> DeferAsync(Guid orgId, string userId, OnboardingPartKey key, DateTime? remindAt, CancellationToken ct);

    /// <summary>
    /// «Save and exit»: keeps the part open and raises the same "To do", due tomorrow at 09:00 in Rome, so what remains never disappears silently.
    /// 422 <see cref="OnboardingPlanCodes.PartNotOpen"/> for a part that is done or not available (nothing remains).
    /// </summary>
    Task<OnboardingPartChange> ExitAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct);

    /// <summary>Clears the deferral of the part and closes its "To do". Harmless on a part that was never deferred or left.</summary>
    Task<OnboardingPartChange> ResumeAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct);
}

/// <summary>The stable codes of the plan: errors, the "To do" entity and the reasons of its items. Never rename one.</summary>
public static class OnboardingPlanCodes
{
    /// <summary>422: another part requires the part, so it cannot be deferred.</summary>
    public const string PartPrerequisite = "onboarding_part_prerequisite";

    /// <summary>404: the route names no part of the plan.</summary>
    public const string PartUnknown = "onboarding_part_unknown";

    /// <summary>422: the reminder is not in the future, or too far in it.</summary>
    public const string RemindAtInvalid = "onboarding_remind_at_invalid";

    /// <summary>422: the part is done or not available, so there is nothing to put off or to keep open: no "To do" is raised for it.</summary>
    public const string PartNotOpen = "onboarding_part_not_open";

    /// <summary>The entity type of the "To do" of a part: its id is the id of the <c>OnboardingPartState</c> row.</summary>
    public const string TodoEntityType = "OnboardingPart";

    /// <summary>The reason of the "To do" of a deferral.</summary>
    public const string ReasonDeferred = "onboarding_part_deferred";

    /// <summary>The reason of the "To do" of an exit.</summary>
    public const string ReasonExit = "onboarding_part_exit";
}

/// <summary>The limits of the plan.</summary>
public static class OnboardingPlanLimits
{
    /// <summary>The furthest a reminder may be, in days from now.</summary>
    public const int MaxRemindDays = 365;

    /// <summary>The hour of the day, in Rome, of the default reminder (tomorrow).</summary>
    public const int DefaultRemindHour = 9;
}
