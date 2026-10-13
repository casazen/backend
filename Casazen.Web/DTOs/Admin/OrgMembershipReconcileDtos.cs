using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Admin;

/// <summary>
/// Report of <c>POST /api/admin/org-members/reconcile</c> (AM-01). Ids, codes and counts only, no names and no emails.
/// Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
public class OrgMembershipReconcileResponse
{
    /// <summary>True: nothing was saved, the report shows what an applied run would do.</summary>
    public bool DryRun { get; set; }

    /// <summary>Host orgs with at least one user.</summary>
    public int HostOrgsScanned { get; set; }

    /// <summary>Org members read, the owners the run creates included.</summary>
    public int MembersScanned { get; set; }

    /// <summary>What the run changes (or, in a dry run, would change).</summary>
    public IReadOnlyList<OrgMembershipFixDto> Fixes { get; set; } = [];

    /// <summary>What needs a decision and is left as it is.</summary>
    public IReadOnlyList<OrgMembershipIssueDto> Issues { get; set; } = [];

    public static OrgMembershipReconcileResponse From(OrgMembershipReconcileReport report) => new()
    {
        DryRun = report.DryRun,
        HostOrgsScanned = report.HostOrgsScanned,
        MembersScanned = report.MembersScanned,
        Fixes = report.Fixes
            .Select(f => new OrgMembershipFixDto { Code = f.Code, UserId = f.UserId, OrgId = f.OrgId, Detail = f.Detail })
            .ToList(),
        Issues = report.Issues
            .Select(i => new OrgMembershipIssueDto { Code = i.Code, OrgId = i.OrgId, UserIds = i.UserIds, Detail = i.Detail })
            .ToList(),
    };
}

public class OrgMembershipFixDto
{
    /// <summary>Stable code (<c>owner_member_created</c>, <c>account_membership_added</c>...).</summary>
    public string Code { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public Guid? OrgId { get; set; }

    public string Detail { get; set; } = string.Empty;
}

public class OrgMembershipIssueDto
{
    /// <summary>Stable code (<c>org_owner_ambiguous</c>, <c>org_user_without_member</c>...).</summary>
    public string Code { get; set; } = string.Empty;

    public Guid? OrgId { get; set; }

    public IReadOnlyList<string> UserIds { get; set; } = [];

    public string Detail { get; set; } = string.Empty;
}
