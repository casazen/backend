using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

public class OrgActivityListQuery
{
    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public string[]? Type { get; set; }

    public string? Area { get; set; }

    public string? Actor { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = OrgActivityRules.DefaultPageSize;
}

public class OrgActivityEntryDto
{
    public Guid Id { get; set; }

    public DateTime When { get; set; }

    public string? ActorUserId { get; set; }

    public string Area { get; set; } = string.Empty;

    public OrgActivityType Type { get; set; }

    public OrgActivitySubjectType SubjectType { get; set; }

    public string SubjectId { get; set; } = string.Empty;

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

public class RequestOrgAccessRequest
{
    [Required(ErrorMessage = "AccessRequestAreaRequired")]
    [MaxLength(40, ErrorMessage = "AccessRequestAreaUnknown")]
    public string Area { get; set; } = string.Empty;

    [MaxLength(OrgAccessRequestRules.MaxNoteLength, ErrorMessage = "AccessRequestNoteTooLong")]
    public string? Note { get; set; }
}

public class OrgAccessRequestedDto
{
    public int Notified { get; set; }

    public static OrgAccessRequestedDto From(OrgAccessRequested requested) => new() { Notified = requested.Notified };
}
