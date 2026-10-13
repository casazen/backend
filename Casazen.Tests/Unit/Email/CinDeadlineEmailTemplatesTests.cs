using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// CO-20 (A5-31): CIN alert email to the host, IT/EN, one text per phase of the configured deadline (never "today" after
/// it), obligation and penalties with their article (art. 13-ter D.L. 145/2023), every dynamic value HTML-encoded (FD-13).
/// </summary>
public class CinDeadlineEmailTemplatesTests
{
    private const string ComplianceUrl = "https://casazen-app.test/app/short-rent/compliance/cin";

    private static readonly DateOnly Deadline = new(2027, 3, 1);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<int?, string, string> ItalianPhases => new()
    {
        { -10, "CIN da inserire entro il 01/03/2027", "Mancano <strong>10 giorni</strong> alla scadenza del 01/03/2027" },
        { -1, "CIN da inserire entro il 01/03/2027", "Manca <strong>1 giorno</strong> alla scadenza del 01/03/2027" },
        { 0, "CIN da inserire: la scadenza del 01/03/2027 è oggi", "è <strong>oggi</strong>" },
        { 200, "CIN mancante: scadenza del 01/03/2027 superata", "è <strong>superata</strong>" },
        { null, "CIN obbligatorio: proprietà senza codice valido", "Queste proprietà non hanno ancora un codice identificativo nazionale (CIN) valido:" },
    };

    [Theory]
    [MemberData(nameof(ItalianPhases))]
    public void CinDeadlineAlert_Italian_TextOfThePhase(int? dayOffset, string subject, string body)
    {
        var content = EmailTemplates.CinDeadlineAlert(EmailTemplates.DefaultCulture, Status(dayOffset), ["Villa Rosa"], ComplianceUrl);

        Assert.Equal(subject, content.Subject);
        Assert.Contains(body, content.HtmlBody);
        Assert.Contains("<li>Villa Rosa</li>", content.HtmlBody);
        Assert.Contains("art. 13-ter, comma 8, D.L. 145/2023, convertito dalla L. 191/2023", content.HtmlBody);
        Assert.Contains("da 800 a 8.000 euro", content.HtmlBody);
        Assert.Contains($"href=\"{ComplianceUrl}\"", content.HtmlBody);
        Assert.Contains("Apri Conformità CIN", content.HtmlBody);
    }

    [Fact]
    public void CinDeadlineAlert_AfterTheDeadline_NeverSaysToday()
    {
        var content = EmailTemplates.CinDeadlineAlert(EmailTemplates.DefaultCulture, Status(200), ["Villa Rosa"], ComplianceUrl);

        Assert.DoesNotContain("oggi", content.Subject + content.HtmlBody);
        Assert.DoesNotContain("Mancano", content.HtmlBody);
    }

    [Fact]
    public void CinDeadlineAlert_NoDeadline_ShowsNoDate()
    {
        var content = EmailTemplates.CinDeadlineAlert(EmailTemplates.DefaultCulture, Status(null), ["Villa Rosa"]);

        Assert.DoesNotContain("scadenza", content.Subject + content.HtmlBody);
        Assert.DoesNotMatch(new Regex(@"\d{2}/\d{2}/\d{4}"), content.Subject + content.HtmlBody);
        Assert.DoesNotContain("<a href", content.HtmlBody);
    }

    [Fact]
    public void CinDeadlineAlert_English_CompleteDocumentWithoutPlaceholders()
    {
        foreach (var offset in new int?[] { -10, -1, 0, 200, null })
        {
            var english = EmailTemplates.CinDeadlineAlert(English, Status(offset), ["Villa Rosa", "Casa Blu"], ComplianceUrl);
            var italian = EmailTemplates.CinDeadlineAlert(EmailTemplates.DefaultCulture, Status(offset), ["Villa Rosa", "Casa Blu"], ComplianceUrl);

            Assert.Contains("<html lang=\"en\">", english.HtmlBody);
            Assert.Contains("art. 13-ter, paragraph 8, Decree-Law 145/2023", english.HtmlBody);
            Assert.Contains("Open CIN compliance", english.HtmlBody);
            Assert.Equal(2, Regex.Matches(english.HtmlBody, "<li>").Count);
            foreach (var content in new[] { english, italian })
                Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
        }

        Assert.Equal(
            "CIN missing: the 1 March 2027 deadline has passed",
            EmailTemplates.CinDeadlineAlert(English, Status(200), ["Villa Rosa"]).Subject);
    }

    [Fact]
    public void CinDeadlineAlert_PropertyNameWithMarkup_IsHtmlEncoded()
    {
        var content = EmailTemplates.CinDeadlineAlert(
            EmailTemplates.DefaultCulture, Status(-10), ["<a href=\"https://phish.example\">Villa</a>"], ComplianceUrl);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Villa&lt;/a&gt;", content.HtmlBody);
    }

    private static CinDeadlineStatus Status(int? dayOffset) =>
        dayOffset is { } offset
            ? CinDeadlineStatus.On(Deadline, Deadline.AddDays(offset))
            : CinDeadlineStatus.On(null, Deadline);
}
