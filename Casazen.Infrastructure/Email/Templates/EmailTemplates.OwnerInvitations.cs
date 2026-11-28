using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OwnerInvitation = "owner-invitation";
    }

    public static EmailContent OwnerInvitation(
        CultureInfo culture,
        string ownerName,
        string agencyName,
        string acceptUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("OwnerInvitation_Greeting", ownerName)
            .Paragraph("OwnerInvitation_Body", agencyName)
            .Button("OwnerInvitation_Cta", acceptUrl)
            .LinkFallback("OwnerInvitation_LinkFallback", acceptUrl)
            .Muted("OwnerInvitation_Expires", builder.FormatInstant(expiresAtUtc))
            .Build("OwnerInvitation_Subject", agencyName) with
        {
            AccountInvitation = new("owner", acceptUrl, expiresAtUtc),
        };
    }
}
