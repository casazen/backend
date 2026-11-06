using Casazen.Core.Models;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The activation plan (NF-104, NF-10, E10/F01/US01/T03) as the API answers it: codes, counts and ids only.</summary>
public static class OnboardingPlanMapping
{
    public static OnboardingPlanDto ToDto(this OnboardingPlan plan) => new()
    {
        Parts = [.. plan.Parts.Select(part => part.ToDto())],
        Needs = [.. plan.Needs],
        Path = plan.Path.ToDto(),
        Activated = plan.Activated,
    };

    public static OnboardingPlanPartDto ToDto(this OnboardingPlanPart part) => new()
    {
        Code = part.Code,
        Order = part.Order,
        State = part.State,
        Reason = part.Reason,
        Done = part.Done,
        Total = part.Total,
        Requires = [.. part.Requires],
        Deferrable = part.Deferrable,
        Needs = [.. part.Needs],
        RemindAt = part.RemindAt,
        Assist = part.Assist is { } assist
            ? new OnboardingAssistDto { Id = assist.Id, Status = assist.Status, Context = assist.Context, RequestedAt = assist.RequestedAt }
            : null,
        LaunchTarget = part.LaunchTarget,
    };

    public static OnboardingPathDto ToDto(this OnboardingPath path) => new()
    {
        Code = path.Code,
        Steps =
        [
            .. path.Steps.Select(step => new OnboardingPathStepDto
            {
                Code = step.Code,
                Order = step.Order,
                Parts = [.. step.Parts],
                State = step.State,
                Visible = step.Visible,
            }),
        ],
        Next = path.Next,
    };

    public static OnboardingPartChangeDto ToDto(this OnboardingPartChange change) => new()
    {
        Code = change.Code,
        State = change.State,
        RemindAt = change.RemindAt,
        TodoId = change.TodoId,
    };
}
