using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Platform-admin maintenance of the org membership (AM-01): the reconcile command. Staff only: the people of an org are
/// managed by the org itself (AM-02), this only repairs what drifted.
/// </summary>
[ApiController]
[Route("api/admin/org-members")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminOrgMembersController(
    IOrgMembershipService orgMembershipService,
    ILogger<AdminOrgMembersController> logger) : ControllerBase
{
    /// <summary>
    /// Gives an org member (owner) row to every host org that has none and re-aligns the memberships with the org roles
    /// (the account membership, the role keys of the rental memberships). What it cannot decide (an org with several
    /// owner candidates, a member of another org than its user's) is reported and left alone, never guessed.
    /// <paramref name="dryRun"/> defaults to true: the report shows what would change and nothing is saved; send
    /// <c>dryRun=false</c> to apply. Idempotent. Runbook <c>docs/runbooks/org-team.md</c>.
    /// </summary>
    [HttpPost("reconcile")]
    [ProducesResponseType(typeof(OrgMembershipReconcileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgMembershipReconcileResponse>> Reconcile(
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var report = await orgMembershipService.ReconcileAsync(dryRun, cancellationToken);

        logger.LogInformation(
            "Admin org-members reconcile (dryRun={DryRun}): hostOrgs={HostOrgs}, members={Members}, fixes={Fixes}, issues={Issues}",
            report.DryRun, report.HostOrgsScanned, report.MembersScanned, report.Fixes.Count, report.Issues.Count);

        return Ok(OrgMembershipReconcileResponse.From(report));
    }
}
