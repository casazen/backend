using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The activation of the flows and the final test (E10/F01/US01/T04) as the API answers them: codes and ids only.</summary>
public static class OnboardingActivationMapping
{
    public static FlowActivationResultDto ToDto(this IReadOnlyList<FlowActivationOutcome> outcomes, OnboardingFlow flow) => new()
    {
        Flow = flow,
        Outcomes =
        [
            .. outcomes.Select(outcome => new FlowActivationOutcomeDto
            {
                PropertyId = outcome.PropertyId,
                Outcome = outcome.Outcome,
                UnmetPrerequisites = [.. outcome.UnmetPrerequisites],
                Reason = outcome.Reason,
            }),
        ],
    };

    public static FlowTestRunDto ToDto(this FlowTestRunResult result) => new()
    {
        PropertyId = result.PropertyId,
        BookingId = result.BookingId,
        Checks = [.. result.Checks.Select(check => new FlowCheckDto { Flow = check.Flow, Outcome = check.Outcome, Reasons = [.. check.Reasons] })],
    };
}
