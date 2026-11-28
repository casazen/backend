using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.DTOs.Orgs;

public class CreateOrgInvitationRequest
{
    [Required(ErrorMessage = "OrgInvitationEmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "OrgInvitationEmailTooLong")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "OrgInvitationNameRequired")]
    [MaxLength(200, ErrorMessage = "OrgInvitationNameTooLong")]
    [RegularExpression(@"^[^<>@/\\\p{C}]*$", ErrorMessage = "OrgInvitationNameInvalid")]
    public string Name { get; set; } = string.Empty;

    public OrgRole Role { get; set; }

    [Required(ErrorMessage = "OrgMemberAreaRequired")]
    public List<string> Areas { get; set; } = [];

    /// <summary><c>All</c> (default) or <c>Selected</c> («Solo alcuni», a collaborator only: 422 <c>org_member_scope_not_supported</c> otherwise). The properties of a <c>Selected</c> collaborator are in <see cref="PropertyIds"/>; with none the person reaches none until the owner gives them (<c>PUT /api/orgs/me/members/{id}/properties</c>).</summary>
    public PropertyScope PropertyScope { get; set; } = PropertyScope.All;

    /// <summary>
    /// The properties a <c>Selected</c> collaborator will reach once it accepts (AM-02c), no more than 500: live properties of the
    /// org that the inviter reaches (422 <c>org_member_property_unknown</c> otherwise). Allowed only with <c>Selected</c> and the
    /// <c>Collaborator</c> role (422 <c>org_member_scope_not_supported</c> for anything else). A property deleted before the
    /// invitation is accepted is skipped without an error.
    /// </summary>
    [MaxLength(500, ErrorMessage = "OrgMemberPropertyIdsTooMany")]
    public List<Guid>? PropertyIds { get; set; }

    [RegularExpression("^(?i:it|en)$", ErrorMessage = "OrgInvitationLanguageInvalid")]
    public string? Language { get; set; }
}

public class OrgInvitationDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public IReadOnlyList<string> Areas { get; set; } = [];
    public PropertyScope PropertyScope { get; set; }

    public IReadOnlyList<Guid> PropertyIds { get; set; } = [];

    public OrgInvitationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When an email of the invitation last went out because of a person's action (UTC): the creation or the last «reinvia»
    /// (AM-02c); null for an invitation written before it was tracked. «Reinvia» is refused for <c>OrgTeam:ResendMinutes</c> after it
    /// (429 <c>invitation_resend_too_soon</c>). The reminder of the third day is the system's and does not move it.
    /// </summary>
    public DateTime? LastSentAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ReminderSentAt { get; set; }

    public string InvitedByUserId { get; set; } = string.Empty;

    public static OrgInvitationDto From(OrgInvitationView view) => new()
    {
        Id = view.Id,
        Email = view.Email,
        Name = view.Name,
        Role = view.Role,
        Areas = view.Areas,
        PropertyScope = view.PropertyScope,
        PropertyIds = view.PropertyIds,
        Status = view.Status,
        CreatedAt = view.CreatedAt,
        LastSentAt = view.LastSentAt,
        ExpiresAt = view.ExpiresAt,
        ReminderSentAt = view.ReminderSentAt,
        InvitedByUserId = view.InvitedByUserId,
    };
}

public class OrgInvitationSentDto
{
    public OrgInvitationDto Invitation { get; set; } = new();
    public bool EmailQueued { get; set; }

    public static OrgInvitationSentDto From(OrgInvitationSent sent) => new()
    {
        Invitation = OrgInvitationDto.From(sent.Invitation),
        EmailQueued = sent.EmailQueued,
    };
}

/// <summary>
/// A fresh link to give to the invited person by hand. It carries the secret of the invitation: the previous link, the one
/// of the email included, stops working. The response is never cached.
/// </summary>
public class OrgInvitationLinkDto
{
    public Guid InvitationId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    public static OrgInvitationLinkDto From(OrgInvitationLink link) => new()
    {
        InvitationId = link.InvitationId,
        Url = link.Url,
        ExpiresAt = link.ExpiresAt,
    };
}

public class OrgMemberDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public OrgMemberStatus Status { get; set; }
    public PropertyScope PropertyScope { get; set; }

    public IReadOnlyList<string> Areas { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? DeactivatedAt { get; set; }

    public static OrgMemberDto From(OrgMemberView view) => new()
    {
        Id = view.Id,
        UserId = view.UserId,
        Email = view.Email,
        FirstName = view.FirstName,
        LastName = view.LastName,
        Role = view.Role,
        Status = view.Status,
        PropertyScope = view.PropertyScope,
        Areas = view.Areas,
        CreatedAt = view.CreatedAt,
        DeactivatedAt = view.DeactivatedAt,
    };
}

public class OrgSeatsDto
{
    public int Max { get; set; }
    public int Used { get; set; }
    public int Available { get; set; }
    public int ActiveMembers { get; set; }
    public int PendingInvitations { get; set; }
    public bool Unlimited { get; set; }
    public bool CanInvite { get; set; }

    public static OrgSeatsDto From(OrgSeatUsage usage) => new()
    {
        Max = usage.Max,
        Used = usage.Used,
        Available = usage.Available,
        ActiveMembers = usage.ActiveMembers,
        PendingInvitations = usage.PendingInvitations,
        Unlimited = usage.IsUnlimited,
        CanInvite = usage.CanInvite,
    };
}

public class OrgMembersResponse
{
    public IReadOnlyList<OrgMemberDto> Items { get; set; } = [];
    public OrgSeatsDto Seats { get; set; } = new();

    public static OrgMembersResponse From(OrgTeamView team) => new()
    {
        Items = team.Members.Select(OrgMemberDto.From).ToList(),
        Seats = OrgSeatsDto.From(team.Seats),
    };
}

public class ChangeOrgMemberRoleRequest
{
    public OrgRole Role { get; set; }
}

public class SetOrgMemberAreasRequest
{
    public List<string>? Areas { get; set; }
}

/// <summary>Body of <c>POST /api/org-invitations/lookup</c>: the secret of the link, in the body so it never appears in a URL.</summary>
public class OrgInvitationLookupRequest
{
    [Required(ErrorMessage = "InvitationTokenRequired")]
    [MaxLength(200, ErrorMessage = "InvitationInvalid")]
    public string Token { get; set; } = string.Empty;
}

public class OrgInvitationLookupResponse
{
    public string OrgName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public IReadOnlyList<string> Areas { get; set; } = [];
    public DateTime ExpiresAt { get; set; }

    public static OrgInvitationLookupResponse From(OrgInvitationPreview preview) => new()
    {
        OrgName = preview.OrgName,
        Email = preview.Email,
        Name = preview.Name,
        Role = preview.Role,
        Areas = preview.Areas,
        ExpiresAt = preview.ExpiresAt,
    };
}

public class AcceptOrgInvitationRequest
{
    [Required(ErrorMessage = "InvitationTokenRequired")]
    [MaxLength(200, ErrorMessage = "InvitationInvalid")]
    public string Token { get; set; } = string.Empty;

    public OnboardingConsentsDto? Consents { get; set; }
}

public class AcceptOrgInvitationResponse
{
    public Guid OrgId { get; set; }
    public string OrgName { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public IReadOnlyList<string> Areas { get; set; } = [];

    public bool LeftEmptyOrg { get; set; }

    public static AcceptOrgInvitationResponse From(OrgInvitationAccepted accepted) => new()
    {
        OrgId = accepted.OrgId,
        OrgName = accepted.OrgName,
        Role = accepted.Role,
        Areas = accepted.Areas,
        LeftEmptyOrg = accepted.LeftEmptyOrg,
    };
}
