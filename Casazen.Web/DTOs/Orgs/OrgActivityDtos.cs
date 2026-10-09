using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

// The activity log of the org (AM-02b) and the requests for access. Enums travel as member names (the API's single enum
// converter), the area as the code of the context; every error message is a key of Resources/SharedResources.resx. The log
// holds ids and codes only, so nothing here carries a name, an email or a free text.

/// <summary>Query of <c>GET /api/orgs/me/activity</c> and <c>.../activity.csv</c>: every field is optional and they combine.</summary>
public class OrgActivityListQuery
{
    /// <summary>Lines at or after this instant (UTC).</summary>
    public DateTime? From { get; set; }

    /// <summary>Lines at or before this instant (UTC).</summary>
    public DateTime? To { get; set; }

    /// <summary>Only these events (<c>MemberRoleChanged</c>...): repeat the parameter or separate the names with commas.</summary>
    public string[]? Type { get; set; }

    /// <summary>Only this area: <c>account</c>, <c>short-rent</c>, <c>long-rent</c> or <c>supplier</c>.</summary>
    public string? Area { get; set; }

    /// <summary>Only what this account did (<c>actorUserId</c> of a line), or <c>system</c> for what no person did.</summary>
    public string? Actor { get; set; }

    /// <summary>The page, from 1.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Lines per page: 50 by default, 100 at most.</summary>
    public int PageSize { get; set; } = OrgActivityRules.DefaultPageSize;
}

/// <summary>
/// One line of the activity log. <c>actorUserId</c> and <c>subjectId</c> are opaque ids: the client resolves them with what it
/// can already see (the people, the properties, the org). <c>actorUserId</c> is null when nobody did it (a job, a webhook).
/// </summary>
public class OrgActivityEntryDto
{
    public Guid Id { get; set; }

    /// <summary>UTC.</summary>
    public DateTime When { get; set; }

    public string? ActorUserId { get; set; }

    /// <summary><c>account</c>, <c>short-rent</c>, <c>long-rent</c> or <c>supplier</c>.</summary>
    public string Area { get; set; } = string.Empty;

    public OrgActivityType Type { get; set; }

    /// <summary>What <c>subjectId</c> is: a member (its account id), an invitation, the org, a property, a supplier.</summary>
    public OrgActivitySubjectType SubjectType { get; set; }

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>A few short codes (a role, a tier, a count), the keys the type allows. Never a name.</summary>
    public IReadOnlyDictionary<string, string> Details { get; set; } = new Dictionary<string, string>();

    public static OrgActivityEntryDto From(OrgActivityItem item) => new()
    {
        Id = item.Id,
        When = item.When,
        ActorUserId = item.ActorUserId,
        Area = OrgActivityCatalog.AreaCode(item.Area),
        Type = item.Type,
        SubjectType = item.SubjectType,
        SubjectId = item.SubjectId,
        Details = item.Details,
    };
}

/// <summary>Body of <c>POST /api/orgs/me/access-requests</c>.</summary>
public class RequestOrgAccessRequest
{
    /// <summary>
    /// What the member asks access to: an area (<c>account</c>, <c>short-rent</c>, <c>long-rent</c>, <c>supplier</c>) or a page
    /// (<c>people</c>, <c>properties</c>, <c>suppliers</c>, <c>billing</c>, <c>organization</c>, <c>activity</c>,
    /// <c>integrations</c>, <c>security</c>, <c>payments</c>, <c>reports</c>, <c>prices</c>). 422 <c>access_request_area_unknown</c> for anything else.
    /// </summary>
    [Required(ErrorMessage = "AccessRequestAreaRequired")]
    [MaxLength(40, ErrorMessage = "AccessRequestAreaUnknown")]
    public string Area { get; set; } = string.Empty;

    /// <summary>A short note for the administrators, 200 characters at most. It goes in their email and is not stored.</summary>
    [MaxLength(OrgAccessRequestRules.MaxNoteLength, ErrorMessage = "AccessRequestNoteTooLong")]
    public string? Note { get; set; }
}

/// <summary>The answer to a request for access: how many administrators were told.</summary>
public class OrgAccessRequestedDto
{
    /// <summary>The administrators an email was queued for; 0 when there was nobody to tell (the request is recorded all the same).</summary>
    public int Notified { get; set; }

    public static OrgAccessRequestedDto From(OrgAccessRequested requested) => new() { Notified = requested.Notified };
}
