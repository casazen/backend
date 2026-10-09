using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>The email of a request for access that a member sends to the administrators of its org (AM-02b).</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OrgAccessRequest = "org-access-request";
    }

    /// <summary>
    /// EmailTexts key of the name of an area or page a member can ask access to (<c>OrgAccessRequestRules.Areas</c>):
    /// <c>OrgAccessArea_</c> and the code in Pascal case (<c>short-rent</c> is <c>OrgAccessArea_ShortRent</c>).
    /// </summary>
    public static string OrgAccessAreaKey(string area) =>
        "OrgAccessArea_" + string.Concat(
            area.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>Name of an area or page a member can ask access to, in <paramref name="culture"/>.</summary>
    public static string OrgAccessAreaLabel(CultureInfo culture, string area) => EmailTexts.Get(OrgAccessAreaKey(area), culture);

    /// <summary>
    /// The request, to an administrator: who asks (name and role), for which area or page, the note it wrote if any, and where
    /// the administrator changes the role or the properties of the person. Says that nothing changes until the administrator
    /// decides, so an email the administrator does not understand can be ignored. The note is the member's own text: it is
    /// HTML-encoded like every dynamic value.
    /// </summary>
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
