using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OrgAccessRequest = "org-access-request";
    }

    public static string OrgAccessAreaKey(string area) =>
        "OrgAccessArea_" + string.Concat(
            area.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    public static string OrgAccessAreaLabel(CultureInfo culture, string area) => EmailTexts.Get(OrgAccessAreaKey(area), culture);

    public static EmailContent OrgAccessRequest(
        CultureInfo culture,
        string recipientName,
        string requesterName,
        OrgRole requesterRole,
        string area,
        string? note,
        string peopleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        var areaLabel = OrgAccessAreaLabel(culture, area);
        return builder
            .Paragraph("OrgAccessRequest_Greeting", recipientName)
            .Paragraph("OrgAccessRequest_Body", requesterName, OrgRoleLabel(culture, requesterRole), areaLabel)
            .Quote("OrgAccessRequest_NoteLabel", note)
            .Muted("OrgAccessRequest_Hint")
            .Button("OrgAccessRequest_Cta", peopleUrl)
            .Build("OrgAccessRequest_Subject", requesterName, areaLabel);
    }
}
