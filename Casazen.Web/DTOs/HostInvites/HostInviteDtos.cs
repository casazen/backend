using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.HostInvites;

public class CreateHostInviteRequest
{
    [Required(ErrorMessage = "HostInviteEmailRequired")]
    [EmailAddress(ErrorMessage = "HostInviteEmailInvalid")]
    [MaxLength(255, ErrorMessage = "HostInviteEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "HostInviteMessageTooLong")]
    public string? Message { get; set; }

    [MaxLength(5, ErrorMessage = "HostInviteLocaleInvalid")]
    public string? Locale { get; set; }
}

public class HostInviteLookupRequest
{
    [Required(ErrorMessage = "HostInviteTokenRequired")]
    [MaxLength(128, ErrorMessage = "HostInviteInvalid")]
    public string Token { get; set; } = string.Empty;
}

public class AcceptHostInviteRequest
{
    [Required(ErrorMessage = "HostInviteTokenRequired")]
    [MaxLength(128, ErrorMessage = "HostInviteInvalid")]
    public string Token { get; set; } = string.Empty;
}

public class HostInviteSentDto
{
    public Guid InviteId { get; set; }
    public DateTime ExpiresAt { get; set; }

    public string? Url { get; set; }

    public static HostInviteSentDto From(HostInviteSent sent) => new() { InviteId = sent.InviteId, ExpiresAt = sent.ExpiresAt, Url = sent.Url };
}

public class HostInviteInviterDto
{
    public string Name { get; set; } = string.Empty;
    public string? OrgName { get; set; }
}

public class HostInviteDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public HostInviteInviterDto? InvitedBy { get; set; }

    public static HostInviteDto From(HostInviteItem item) => new()
    {
        Id = item.Id,
        Email = item.Email,
        State = item.State.ToString(),
        Message = item.Message,
        CreatedAt = item.CreatedAt,
        ExpiresAt = item.ExpiresAt,
        UsedAt = item.UsedAt,
        InvitedBy = item.InvitedBy is null ? null : new HostInviteInviterDto { Name = item.InvitedBy.Name, OrgName = item.InvitedBy.OrgName },
    };
}

public class HostInviteQuotaDto
{
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
    public DateTime ResetsAt { get; set; }

    public static HostInviteQuotaDto From(HostInviteQuota quota) =>
        new() { Limit = quota.Limit, Used = quota.Used, Remaining = quota.Remaining, ResetsAt = quota.ResetsAt };
}

public class HostInviteLookupResponse
{
    public bool IsAgency { get; set; }
    public bool Valid { get; set; }

    public string State { get; set; } = string.Empty;

    public string? InviterName { get; set; }

    public string? MaskedEmail { get; set; }

    public static HostInviteLookupResponse From(HostInviteLookup lookup) => new()
    {
        IsAgency = lookup.IsAgency,
        Valid = lookup.Valid,
        State = lookup.State,
        InviterName = lookup.InviterName,
        MaskedEmail = lookup.MaskedEmail,
    };
}
