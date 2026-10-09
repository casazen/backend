using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>The emails of the org invitations (AM-02): the invitation, the reminder of the third day and the note of the expiry.</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OrgInvitation = "org-invitation";
        public const string OrgInvitationReminder = "org-invitation-reminder";
        public const string OrgInvitationExpired = "org-invitation-expired";
    }

    /// <summary>EmailTexts key of the name of an <see cref="OrgRole"/>.</summary>
    public static string OrgRoleKey(OrgRole role) => $"OrgRole_{role}";

    /// <summary>EmailTexts key of the name of an area (<c>short-rent</c>, <c>long-rent</c>).</summary>
    public static string OrgAreaKey(string area) =>
        string.Equals(area, OrgRoleCatalog.LongRent, StringComparison.OrdinalIgnoreCase) ? "OrgArea_LongRent" : "OrgArea_ShortRent";

    /// <summary>Name of a role in <paramref name="culture"/> (Titolare, Amministratore, Property manager, Collaboratore, Contabile).</summary>
    public static string OrgRoleLabel(CultureInfo culture, OrgRole role) => EmailTexts.Get(OrgRoleKey(role), culture);

    /// <summary>
    /// The invitation, to the invited person. Says who invites, to which org, with which role and where, and that the link
    /// works only for this address and only once. <paramref name="acceptUrl"/> carries the secret token (it is built by
    /// <see cref="PublicSiteLinks.OrgInvitationAccept"/>); the expiry is shown in Italian time (Europe/Rome).
    /// </summary>
    public static EmailContent OrgInvitation(
        CultureInfo culture,
        string inviteeName,
        string inviterName,
        string orgName,
        OrgRole role,
        IReadOnlyCollection<string> areas,
        string inviteeEmail,
        string acceptUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        var areaNames = string.Join(", ", areas.Select(area => EmailTexts.Get(OrgAreaKey(area), culture)));
        return builder
            .Paragraph("OrgInvitation_Greeting", inviteeName)
            .Paragraph("OrgInvitation_Body", inviterName, OrgRoleLabel(culture, role), orgName, areaNames)
            .Muted("OrgInvitation_EmailHint", inviteeEmail)
            .Button("OrgInvitation_Cta", acceptUrl)
            .LinkFallback("OrgInvitation_LinkFallback", acceptUrl)
            .Muted("OrgInvitation_Expires", builder.FormatInstant(expiresAtUtc), inviterName)
            .Build("OrgInvitation_Subject", inviterName, orgName);
    }

    /// <summary>
    /// The reminder of the third day, to the invited person. Its link replaces the one of the first email (the token is
    /// rotated: only the hash is stored), and the email says so.
    /// </summary>
    public static EmailContent OrgInvitationReminder(
        CultureInfo culture,
        string inviteeName,
        string orgName,
        OrgRole role,
        string acceptUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("OrgInvitationReminder_Greeting", inviteeName)
            .Paragraph("OrgInvitationReminder_Body", orgName, OrgRoleLabel(culture, role))
            .Button("OrgInvitationReminder_Cta", acceptUrl)
            .LinkFallback("OrgInvitationReminder_LinkFallback", acceptUrl)
            .Muted("OrgInvitationReminder_NewLink")
            .Muted("OrgInvitationReminder_Expires", builder.FormatInstant(expiresAtUtc))
            .Build("OrgInvitationReminder_Subject", orgName, builder.FormatDate(expiresAtUtc));
    }

    /// <summary>
    /// The note of the expiry, to the person who invited: nobody accepted, the seat is free again, a new invitation can go
    /// out. Names the invitee and its address (the inviter wrote them), and links the people page of the account.
    /// </summary>
    public static EmailContent OrgInvitationExpired(
        CultureInfo culture,
        string inviterName,
        string inviteeName,
        string inviteeEmail,
        string peopleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("OrgInvitationExpired_Greeting", inviterName)
            .Paragraph("OrgInvitationExpired_Body", inviteeName, inviteeEmail)
            .Muted("OrgInvitationExpired_Hint")
            .Button("OrgInvitationExpired_Cta", peopleUrl)
            .Build("OrgInvitationExpired_Subject", inviteeName);
    }
}
