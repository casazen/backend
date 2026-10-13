using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The three emails of an org invitation (AM-02), shared by the service that creates and sends it again and by the
/// maintenance job, so the same invitation always reads the same. The language is the one stored on the invitation; the
/// note to the inviter is in the product language (Italian), since nobody chose one for it.
/// </summary>
internal static class OrgInvitationEmails
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>The culture of an invitation language (<c>it</c> or <c>en</c>).</summary>
    public static CultureInfo CultureOf(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : EmailTemplates.DefaultCulture;

    /// <summary>
    /// The invitation for <paramref name="token"/> (the secret of the link, only ever given out in this email or by copying
    /// the link). Rendered before anything is saved: a missing <c>App:PublicSiteBaseUrl</c> is a configuration error, not
    /// an invitation with a wrong link.
    /// </summary>
    public static EmailContent Invitation(
        PublicSiteLinks links,
        OrgInvitation invitation,
        string token,
        string orgName,
        string inviterName) =>
        EmailTemplates.OrgInvitation(
            CultureOf(invitation.Language),
            invitation.Name,
            inviterName,
            orgName,
            invitation.Role,
            invitation.Areas,
            invitation.Email,
            links.OrgInvitationAccept(token),
            invitation.ExpiresAt);

    /// <summary>The reminder of the third day, with the fresh link that replaces the one of the first email.</summary>
    public static EmailContent Reminder(PublicSiteLinks links, OrgInvitation invitation, string token, string orgName) =>
        EmailTemplates.OrgInvitationReminder(
            CultureOf(invitation.Language),
            invitation.Name,
            orgName,
            invitation.Role,
            links.OrgInvitationAccept(token),
            invitation.ExpiresAt);

    /// <summary>The note to the inviter: nobody accepted, the seat is free again.</summary>
    public static EmailContent Expired(PublicSiteLinks links, OrgInvitation invitation, string inviterName) =>
        EmailTemplates.OrgInvitationExpired(
            EmailTemplates.DefaultCulture,
            inviterName,
            invitation.Name,
            invitation.Email,
            links.AccountPeople());
}
