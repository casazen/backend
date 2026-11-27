using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Admin;

public class OrgMembershipReconcileResponse
{
    public bool DryRun { get; set; }

    public int HostOrgsScanned { get; set; }

    public int MembersScanned { get; set; }

    public IReadOnlyList<OrgMembershipFixDto> Fixes { get; set; } = [];

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
    public string Code { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public Guid? OrgId { get; set; }

    public string Detail { get; set; } = string.Empty;
}

public class OrgMembershipIssueDto
{
    public string Code { get; set; } = string.Empty;

    public Guid? OrgId { get; set; }

    public IReadOnlyList<string> UserIds { get; set; } = [];

    public string Detail { get; set; } = string.Empty;
}
