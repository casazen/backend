using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.OwnerInvitations;

/// <summary>
/// Body of <c>POST api/onboarding/owners/invitations/preview</c> (E10/F02/US02/T02): which owners to preview. Absent or <c>null</c> = every owner
/// inside the caller's reach; <b>an empty list is refused</b> (422 <c>owner_invitation_selection_empty</c>), it never means «all».
/// </summary>
public class OwnerInvitationPreviewRequest
{
    public List<Guid>? OwnerIds { get; set; }
}

/// <summary>
/// Body of <c>POST api/onboarding/owners/invitations/confirm</c>: the same selection as the preview and its <c>fingerprint</c>. Without the
/// fingerprint of a preview (422 <c>owner_invitation_preview_required</c>) or with one that is out of date (409 <c>owner_invitation_preview_stale</c>)
/// nothing is sent.
/// </summary>
public class OwnerInvitationConfirmRequest
{
    public List<Guid>? OwnerIds { get; set; }

    [MaxLength(128)]
    public string? Fingerprint { get; set; }
}

/// <summary>
/// The preview of a batch (nothing is sent): <c>recipients</c> are the owners that would get a link, <c>exclusions</c> the ones left out with their
/// reasons (the facts of the portal readiness, <c>already_invited</c>, <c>already_accepted</c>, <c>owner_not_found</c>), <c>quota</c> the invitations
/// of the last 24 hours against the limit of the day, <c>fingerprint</c> what the confirmation must carry.
/// </summary>
public sealed record OwnerInvitationPreviewDto(
    string Fingerprint,
    IReadOnlyList<OwnerInvitationRecipientDto> Recipients,
    IReadOnlyList<OwnerInvitationExclusionDto> Exclusions,
    OwnerInvitationQuotaDto Quota)
{
    public static OwnerInvitationPreviewDto From(OwnerInvitationPreview preview) => new(
        preview.Fingerprint,
        [.. preview.Recipients.Select(r => new OwnerInvitationRecipientDto(r.OwnerId, r.DisplayName, r.Email, r.PortalState))],
        [.. preview.Exclusions.Select(x => new OwnerInvitationExclusionDto(x.OwnerId, x.DisplayName, x.Reasons))],
        new OwnerInvitationQuotaDto(preview.Quota.Limit, preview.Quota.Used, preview.Quota.Remaining));
}

/// <summary>An owner the batch would invite, with the address the link would go to.</summary>
public sealed record OwnerInvitationRecipientDto(Guid OwnerId, string DisplayName, string Email, string PortalState);

/// <summary>An owner the batch leaves out, and the codes that say why. <c>displayName</c> is null for an id that is not an owner of the caller.</summary>
public sealed record OwnerInvitationExclusionDto(Guid OwnerId, string? DisplayName, IReadOnlyList<string> Reasons);

/// <summary>The limit of the day: at most <c>limit</c> invitations for each org in 24 hours.</summary>
public sealed record OwnerInvitationQuotaDto(int Limit, int Used, int Remaining);

/// <summary>
/// The answer of the confirmation: <c>sent</c> invitations (each with its id and expiry) and the owners whose e-mail could not be queued
/// (<c>failed</c>, code <c>owner_invitation_email_not_queued</c>), whose invitation was withdrawn at once.
/// </summary>
public sealed record OwnerInvitationBatchResultDto(int Sent, IReadOnlyList<OwnerInvitationSentDto> Invitations, IReadOnlyList<OwnerInvitationFailureDto> Failed)
{
    public static OwnerInvitationBatchResultDto From(OwnerInvitationBatchResult result) => new(
        result.Sent,
        [.. result.Invitations.Select(i => new OwnerInvitationSentDto(i.InvitationId, i.OwnerId, i.ExpiresAt))],
        [.. result.Failed.Select(f => new OwnerInvitationFailureDto(f.OwnerId, f.Code))]);
}

/// <summary>One invitation of a batch.</summary>
public sealed record OwnerInvitationSentDto(Guid InvitationId, Guid OwnerId, DateTime ExpiresAt);

/// <summary>An owner of a batch whose e-mail was not queued.</summary>
public sealed record OwnerInvitationFailureDto(Guid OwnerId, string Code);

/// <summary>
/// An owner and where its invitation stands: <c>state</c> is <c>notReady</c>, <c>ready</c>, <c>invited</c>, <c>accepted</c>, <c>expired</c> or
/// <c>revoked</c>; the dates are those of the invitation that says it (the accepted one, else the latest).
/// </summary>
public sealed record OwnerInvitationStatusDto(
    Guid OwnerId,
    string DisplayName,
    string State,
    Guid? InvitationId,
    DateTime? SentAt,
    DateTime? ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? ClosedAt)
{
    public static OwnerInvitationStatusDto From(OwnerInvitationStatusItem item) => new(
        item.OwnerId,
        item.DisplayName,
        item.State,
        item.InvitationId,
        item.SentAt,
        item.ExpiresAt,
        item.AcceptedAt,
        item.ClosedAt);
}

/// <summary>Body of <c>POST api/owner-invitations/accept</c>: the secret token of the link, in the body and never in a URL (URLs end up in logs and history).</summary>
public class AcceptOwnerInvitationRequest
{
    [Required(ErrorMessage = "InvitationTokenRequired")]
    [MaxLength(200, ErrorMessage = "InvitationInvalid")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// The owner is now the account's: the claims of the owner portal (<c>owner_id</c>, <c>agency_ids</c>, <c>owned_property_ids</c>) and whether they were
/// handed to the identity provider (<c>claimsRefresh</c>: <c>published</c>, <c>not_configured</c>, <c>failed</c>). When they were published the client
/// asks Auth0 for a new token (<c>cacheMode: 'off'</c>) and goes to the portal. <c>alreadyAccepted</c> is true when the same account had accepted
/// this link before: the answer is then the same.
/// </summary>
public sealed record OwnerInvitationAcceptedDto(
    Guid OwnerUserId,
    IReadOnlyList<Guid> AgencyIds,
    IReadOnlyList<Guid> OwnedPropertyIds,
    bool AlreadyAccepted,
    string ClaimsRefresh)
{
    public static OwnerInvitationAcceptedDto From(OwnerInvitationAccepted accepted) => new(
        accepted.OwnerUserId,
        accepted.AgencyIds,
        accepted.PropertyIds,
        accepted.AlreadyAccepted,
        accepted.ClaimsRefresh);
}
