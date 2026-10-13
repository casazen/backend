using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.HostInvites;

/// <summary>Body of the creation of a host invite (admin and host).</summary>
public class CreateHostInviteRequest
{
    [Required(ErrorMessage = "HostInviteEmailRequired")]
    [EmailAddress(ErrorMessage = "HostInviteEmailInvalid")]
    [MaxLength(255, ErrorMessage = "HostInviteEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "HostInviteMessageTooLong")]
    public string? Message { get; set; }

    /// <summary>Language of the email: <c>it</c> (default) or <c>en</c>.</summary>
    [MaxLength(5, ErrorMessage = "HostInviteLocaleInvalid")]
    public string? Locale { get; set; }
}

/// <summary>Body of the public lookup, for clients that prefer the token out of the URL.</summary>
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

    public static HostInviteSentDto From(HostInviteSent sent) => new() { InviteId = sent.InviteId, ExpiresAt = sent.ExpiresAt };
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

    /// <summary><c>Pending</c>, <c>Used</c>, <c>Expired</c> or <c>Revoked</c>.</summary>
    public string State { get; set; } = string.Empty;

    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    /// <summary>Who invited: filled in the admin list, null in the host's own list.</summary>
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
    public bool Valid { get; set; }

    /// <summary><c>Pending</c>, <c>Used</c>, <c>Expired</c>, <c>Revoked</c> or <c>Invalid</c>.</summary>
    public string State { get; set; } = string.Empty;

    public string? InviterName { get; set; }

    /// <summary>The address with its local part hidden (<c>m***@example.com</c>), only for a valid invite. Never the whole email.</summary>
    public string? MaskedEmail { get; set; }

    public static HostInviteLookupResponse From(HostInviteLookup lookup) => new()
    {
        Valid = lookup.Valid,
        State = lookup.State,
        InviterName = lookup.InviterName,
        MaskedEmail = lookup.MaskedEmail,
    };
}
