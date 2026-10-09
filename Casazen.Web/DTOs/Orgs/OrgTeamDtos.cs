using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.DTOs.Orgs;

// The org team (AM-02): invitations, members and seats. Enums travel as member names (the API's single enum converter);
// every error message is a key of Resources/SharedResources.resx. Nothing here ever carries the token of an invitation,
// except OrgInvitationLinkDto (the link a manager asks for) and the email that is sent to the invitee.

/// <summary>Body of <c>POST /api/orgs/me/invitations</c>.</summary>
public class CreateOrgInvitationRequest
{
    /// <summary>The address the invitation is for; the person must accept with an account that has this very email.</summary>
    [Required(ErrorMessage = "OrgInvitationEmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "OrgInvitationEmailTooLong")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Name and surname of the invited person, as the inviter knows them (no links, no addresses: it goes into an email).</summary>
    [Required(ErrorMessage = "OrgInvitationNameRequired")]
    [MaxLength(200, ErrorMessage = "OrgInvitationNameTooLong")]
    [RegularExpression(@"^[^<>@/\\\p{C}]*$", ErrorMessage = "OrgInvitationNameInvalid")]
    public string Name { get; set; } = string.Empty;

    /// <summary><c>Admin</c> (owner only), <c>PropertyManager</c>, <c>Collaborator</c> or <c>Accountant</c>. Never <c>Owner</c>.</summary>
    public OrgRole Role { get; set; }

    /// <summary>The areas the person works in: <c>short-rent</c> and/or <c>long-rent</c>. At least one.</summary>
    [Required(ErrorMessage = "OrgMemberAreaRequired")]
    public List<string> Areas { get; set; } = [];

    /// <summary><c>All</c> (default) or <c>Selected</c>: the list of properties arrives with AM-03.</summary>
    public PropertyScope PropertyScope { get; set; } = PropertyScope.All;

    /// <summary>Language of the emails: <c>it</c> (default) or <c>en</c>.</summary>
    [RegularExpression("^(?i:it|en)$", ErrorMessage = "OrgInvitationLanguageInvalid")]
    public string? Language { get; set; }
}

/// <summary>An invitation as the team page shows it. <c>Status</c> is <c>Pending</c> while it can be accepted, <c>Expired</c> after.</summary>
public class OrgInvitationDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public IReadOnlyList<string> Areas { get; set; } = [];
    public PropertyScope PropertyScope { get; set; }
    public OrgInvitationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>When the link stops working (UTC).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the reminder of the third day went out (UTC); null until then.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Who invited (<c>User.Id</c>); the client resolves the name from the members it already has.</summary>
    public string InvitedByUserId { get; set; } = string.Empty;

    public static OrgInvitationDto From(OrgInvitationView view) => new()
    {
        Id = view.Id,
        Email = view.Email,
        Name = view.Name,
        Role = view.Role,
        Areas = view.Areas,
        PropertyScope = view.PropertyScope,
        Status = view.Status,
        CreatedAt = view.CreatedAt,
        ExpiresAt = view.ExpiresAt,
        ReminderSentAt = view.ReminderSentAt,
        InvitedByUserId = view.InvitedByUserId,
    };
}

/// <summary>
/// An invitation just created or sent again. <c>EmailQueued</c> is false when the email could not be queued: the invitation
/// exists, its link can be copied (<c>POST …/{id}/link</c>) or the invitation sent again.
/// </summary>
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

/// <summary>A member of the org. <c>Id</c> is the member row (what the endpoints address), <c>UserId</c> the account.</summary>
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

    /// <summary>The areas the person works in: <c>short-rent</c>, <c>long-rent</c>.</summary>
    public IReadOnlyList<string> Areas { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>When the access was switched off (UTC); null while active.</summary>
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

/// <summary>
/// The seats of the org: the people the plan allows and the people it has. <c>Used</c> = <c>ActiveMembers</c> +
/// <c>PendingInvitations</c>. <c>Max</c> and <c>Available</c> are <c>2147483647</c> (int max) when the plan is unlimited
/// (<c>Unlimited</c> says so). With a subscription not in good standing <c>Max</c> is the Starter number and <c>Used</c> can
/// be above it: the members stay, new invitations are refused.
/// </summary>
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

/// <summary><c>GET /api/orgs/me/members</c>: the org's people, owner first, and its seats.</summary>
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

/// <summary>Body of <c>PUT /api/orgs/me/members/{id}</c>: the new role. Areas and scope stay as they are.</summary>
public class ChangeOrgMemberRoleRequest
{
    /// <summary><c>Admin</c> (owner only), <c>PropertyManager</c>, <c>Collaborator</c> or <c>Accountant</c>. Never <c>Owner</c>.</summary>
    public OrgRole Role { get; set; }
}

/// <summary>Body of <c>POST /api/org-invitations/lookup</c>: the secret of the link, in the body so it never appears in a URL.</summary>
public class OrgInvitationLookupRequest
{
    [Required(ErrorMessage = "InvitationTokenRequired")]
    [MaxLength(200, ErrorMessage = "InvitationInvalid")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>What the invited person sees before accepting. Nothing about who invited.</summary>
public class OrgInvitationLookupResponse
{
    public string OrgName { get; set; } = string.Empty;

    /// <summary>The address the invitation is for: the account that accepts must have it.</summary>
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

/// <summary>Body of <c>POST /api/org-invitations/accept</c>: the secret of the link and the four consents of the onboarding.</summary>
public class AcceptOrgInvitationRequest
{
    [Required(ErrorMessage = "InvitationTokenRequired")]
    [MaxLength(200, ErrorMessage = "InvitationInvalid")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Terms of Service, Privacy notice, DPA and subprocessors, accepted again for the org the person joins. Missing or
    /// incomplete: 400 <c>consents_incomplete</c>, like the onboarding; outdated versions: 400 <c>stale_documents</c>.
    /// </summary>
    public OnboardingConsentsDto? Consents { get; set; }
}

/// <summary>The person is now a member of the org.</summary>
public class AcceptOrgInvitationResponse
{
    public Guid OrgId { get; set; }
    public string OrgName { get; set; } = string.Empty;
    public OrgRole Role { get; set; }
    public IReadOnlyList<string> Areas { get; set; } = [];

    /// <summary>True when the person had an empty org of its own, created automatically, and left it for this one.</summary>
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
