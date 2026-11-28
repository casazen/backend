using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string GuestCancelLink = "guest-cancel-link";
    }

    public static EmailContent GuestCancelLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string cancelUrl,
        DateTime expiresAtUtc,
        GuestEmailBranding? branding = null)
    {
        var builder = new EmailHtmlBuilder(culture, branding);
        return builder
            .Paragraph("GuestCancelLink_Greeting", guestName)
            .Paragraph("GuestCancelLink_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestCancelLink_Policy")
            .Button("GuestCancelLink_Cta", cancelUrl)
            .LinkFallback("GuestCancelLink_LinkFallback", cancelUrl)
            .Muted("GuestCancelLink_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("GuestCancelLink_NotYou")
            .Build("GuestCancelLink_Subject", propertyName);
    }
}
