using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The plan of the activation of CasaZen with Octorate (NF-104, NF-10, ONB-01, UX-10, E10/F01/US01/T03): the twelve parts of
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
    Task<OnboardingPlan> GetPlanAsync(Guid orgId, HostScope scope, CancellationToken ct);

    Task<OnboardingPath> GetPathAsync(Guid orgId, HostScope scope, CancellationToken ct);

    Task<OnboardingPartChange> DeferAsync(Guid orgId, string userId, OnboardingPartKey key, DateTime? remindAt, CancellationToken ct);

    Task<OnboardingPartChange> ExitAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct);

    Task<OnboardingPartChange> ResumeAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct);
}

public static class OnboardingPlanCodes
{
    public const string PartPrerequisite = "onboarding_part_prerequisite";

    public const string PartUnknown = "onboarding_part_unknown";

    public const string RemindAtInvalid = "onboarding_remind_at_invalid";

    public const string PartNotOpen = "onboarding_part_not_open";

    public const string TodoEntityType = "OnboardingPart";

    public const string ReasonDeferred = "onboarding_part_deferred";

    public const string ReasonExit = "onboarding_part_exit";
}

public static class OnboardingPlanLimits
{
    public const int MaxRemindDays = 365;

    public const int DefaultRemindHour = 9;
}
