using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The one mapping of what <see cref="IOrgDomainService"/> answers to the answers of the API: 404, 403 <c>plan_required</c>, 409 <c>org_domain_in_use</c>, 400
/// <c>org_domain_invalid</c>, 400 <c>org_domain_not_configured</c> and the others. The console (<see cref="OrgDomainController"/>) and the plugin of a connected
/// site (<see cref="SiteApiController"/>, S023) both answer through it, so the same outcome has the same status and the same <c>code</c> on both and the rules of
/// the plan, of the domain in use and of the validation stay in the service.
/// </summary>
/// <remarks>
/// Each method returns the failure to answer, or <c>null</c> when the outcome is a success that carries its result: the caller then maps the result. The
/// codes are the constants of <see cref="OrgDomainController"/>.
/// </remarks>
public static class OrgDomainOutcomeResults
{
    /// <summary>The <c>code</c> of the 403 for an org whose plan has no custom domain.</summary>
    public const string PlanRequiredCode = "plan_required";

    /// <summary>The plan that has the custom domain, as the 403 says it (<c>requiredPlan</c>): Pro, and Scale which includes it.</summary>
    public const string RequiredPlan = "Pro";

    /// <summary>403 <c>plan_required</c> with <c>requiredPlan</c>.</summary>
    public static ObjectResult PlanRequired(this ControllerBase controller) =>
        controller.StatusCode(StatusCodes.Status403Forbidden, new { code = PlanRequiredCode, requiredPlan = RequiredPlan });

    /// <summary>The answer to a refused <see cref="IOrgDomainService.SetDomainAsync"/>; <c>null</c> for a success with its configuration.</summary>
    public static ActionResult? DomainFailure(this ControllerBase controller, SetOrgDomainResult result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            SetOrgDomainOutcome.Success when result.Config is not null => null,
            SetOrgDomainOutcome.NotFound => controller.NotFound(),
            SetOrgDomainOutcome.PlanRequired => controller.PlanRequired(),
            SetOrgDomainOutcome.Conflict => controller.ApiProblem(
                StatusCodes.Status409Conflict, OrgDomainController.DomainInUseCode, "OrgDomainInUse"),
            SetOrgDomainOutcome.ValidationError => controller.ApiProblem(
                StatusCodes.Status400BadRequest, OrgDomainController.DomainInvalidCode, result.ErrorKey ?? "OrgDomainInvalid"),
            SetOrgDomainOutcome.SubdomainsNotConfigured => controller.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                OrgDomainController.SubdomainsNotConfiguredCode,
                "OrgDomainSubdomainsNotConfigured"),
            _ => controller.ApiProblem(
                StatusCodes.Status400BadRequest, OrgDomainController.DomainInvalidCode, result.ErrorKey ?? "OrgDomainInvalid"),
        };
    }

    /// <summary>The answer to a refused <see cref="IOrgDomainService.VerifyDomainAsync"/>; <c>null</c> for a success with its verification.</summary>
    public static ActionResult? DomainFailure(this ControllerBase controller, VerifyOrgDomainResult result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            VerifyOrgDomainOutcome.Success when result.Verification is not null => null,
            VerifyOrgDomainOutcome.NotFound => controller.NotFound(),
            VerifyOrgDomainOutcome.PlanRequired => controller.PlanRequired(),
            VerifyOrgDomainOutcome.NotConfigured => controller.ApiProblem(
                StatusCodes.Status400BadRequest, OrgDomainController.DomainNotConfiguredCode, "OrgDomainNotConfigured"),
            _ => controller.ApiProblem(
                StatusCodes.Status400BadRequest, OrgDomainController.DomainVerificationFailedCode, "OrgDomainVerificationFailed"),
        };
    }

    /// <summary>The answer to a refused <see cref="IOrgDomainService.SetSurfacesAsync"/>; <c>null</c> for a success with its configuration.</summary>
    public static ActionResult? DomainFailure(this ControllerBase controller, SetOrgDomainSurfacesResult result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            SetOrgDomainSurfacesOutcome.Success when result.Config is not null => null,
            SetOrgDomainSurfacesOutcome.NotFound => controller.NotFound(),
            SetOrgDomainSurfacesOutcome.PlanRequired => controller.PlanRequired(),
            _ => controller.ApiProblem(
                StatusCodes.Status400BadRequest, OrgDomainController.DomainNotConfiguredCode, "OrgDomainNotConfigured"),
        };
    }
}
