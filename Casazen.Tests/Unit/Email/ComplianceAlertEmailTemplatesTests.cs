using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12): the texts of the new stay stages and of the alerts about a period and the connection, email and push. Every dynamic value
/// is HTML-encoded in the email, both languages are complete, and what is in them is the property, the month, the day: no guest.
/// </summary>
public class ComplianceAlertEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private static readonly DateTime CheckIn = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Deadline = new(2026, 11, 10);

    public static TheoryData<string> Templates => new()
    {
        "receipt-missing", "short-stay", "short-stay-arrived", "over-thirty", "istat-approaching", "istat-overdue", "istat-reminder", "tax-approaching",
        "tax-overdue", "connection", "connection-reminder",
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Render_PayloadInTheName_IsHtmlEncoded(string template)
    {
        var content = Render(template, "it-IT", Payload);

        Assert.DoesNotContain("phish.example\">", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Render_ItalianAndEnglish_AreCompleteAndDifferent(string template)
    {
        var italian = Render(template, "it-IT", "Villa Rosa");
        var english = Render(template, "en", "Villa Rosa");

        Assert.Contains("<html lang=\"it\">", italian.HtmlBody);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
        foreach (var content in new[] { italian, english })
        {
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
            Assert.Contains("Villa Rosa", content.Subject);
        }
    }

    [Fact]
    public void ReceiptMissing_SaysTheCommunicationIsSentOrEntrusted_AndTheReceiptIsMissing_InBothLanguages()
    {
        var content = EmailTemplates.ReceiptMissing(EmailTemplates.DefaultCulture, "Villa Rosa", CheckIn);
        var english = EmailTemplates.ReceiptMissing(CultureInfo.GetCultureInfo("en"), "Villa Rosa", CheckIn);

        Assert.Equal("Ricevuta Alloggiati Web mancante - Villa Rosa (05/11/2026)", content.Subject);
        // A stay only entrusted to the channel manager has no report: CasaZen does not know that it was sent, so the text never says only «sent».
        Assert.Contains("risulta inviata o affidata al channel manager", content.HtmlBody);
        Assert.Contains("non c'è ancora la ricevuta", content.HtmlBody);
        Assert.Contains("is recorded as sent or entrusted to the channel manager", english.HtmlBody);
        Assert.Contains("does not hold its receipt yet", english.HtmlBody);
        Assert.DoesNotContain("href=", content.HtmlBody);
    }

    [Fact]
    public void ShortStayAtRisk_ShowsTheExactDeadlineInItalianTime_OnlyWhenItIsKnown()
    {
        var known = EmailTemplates.ShortStayAtRisk(EmailTemplates.DefaultCulture, "Villa Rosa", CheckIn, new DateTime(2026, 11, 5, 19, 0, 0, DateTimeKind.Utc));
        var unknown = EmailTemplates.ShortStayAtRisk(EmailTemplates.DefaultCulture, "Villa Rosa", CheckIn);

        Assert.Contains("Scadenza: <strong>05/11/2026 20:00</strong>", known.HtmlBody);
        Assert.DoesNotContain("Scadenza:", unknown.HtmlBody);
        Assert.Contains("entro 6 ore", known.HtmlBody);
    }

    [Fact]
    public void CompliancePeriodAlert_NamesThePropertyTheMonthAndTheDeadlineDay_InBothLanguages()
    {
        var alert = new ComplianceAlert(ComplianceAlertType.IstatPeriod, Guid.NewGuid(), ComplianceAlertKind.PeriodDeadlineApproaching, "2026-10", Deadline);

        var italian = EmailTemplates.CompliancePeriodAlert(EmailTemplates.DefaultCulture, alert, "Villa Rosa");
        var english = EmailTemplates.CompliancePeriodAlert(CultureInfo.GetCultureInfo("en"), alert, "Villa Rosa");

        Assert.Equal("ISTAT in scadenza - Villa Rosa (ottobre 2026)", italian.Subject);
        Assert.Contains("<strong>ottobre 2026</strong>", italian.HtmlBody);
        Assert.Contains("scade il <strong>10/11/2026</strong>", italian.HtmlBody);
        Assert.Equal("ISTAT report due - Villa Rosa (October 2026)", english.Subject);
        Assert.Contains("due on <strong>10 November 2026</strong>", english.HtmlBody);
        Assert.DoesNotContain("Promemoria", italian.HtmlBody);
    }

    [Fact]
    public void CompliancePeriodAlert_AReminderShowsItsNumber_AndTheButtonLeadsToTheCompliancePage()
    {
        const string url = "https://casazen-app.test/app/short-rent/compliance";
        var alert = new ComplianceAlert(
            ComplianceAlertType.TouristTaxPeriod, Guid.NewGuid(), ComplianceAlertKind.PeriodOverdue, "2026-10", Deadline, ReminderNumber: 2, MaxReminders: 3);

        var content = EmailTemplates.CompliancePeriodAlert(EmailTemplates.DefaultCulture, alert, "Villa Rosa", url);

        Assert.Equal("Imposta di soggiorno scaduta - Villa Rosa (ottobre 2026)", content.Subject);
        Assert.Contains("Promemoria 2 di 3.", content.HtmlBody);
        Assert.Contains($"href=\"{url}\"", content.HtmlBody);
        Assert.Contains("Apri gli adempimenti", content.HtmlBody);
    }

    [Fact]
    public void CompliancePush_EachAlert_HasItsOwnPlainText()
    {
        var culture = EmailTemplates.DefaultCulture;
        var propertyId = Guid.NewGuid();

        var approaching = EmailTemplates.CompliancePush(
            culture, new ComplianceAlert(ComplianceAlertType.IstatPeriod, propertyId, ComplianceAlertKind.PeriodDeadlineApproaching, "2026-10", Deadline), "Villa <Rosa>");
        var overdue = EmailTemplates.CompliancePush(
            culture, new ComplianceAlert(ComplianceAlertType.TouristTaxPeriod, propertyId, ComplianceAlertKind.PeriodOverdue, "2026-10", Deadline), "Villa <Rosa>");
        var connection = EmailTemplates.CompliancePush(
            culture, new ComplianceAlert(ComplianceAlertType.ConnectionInactive, propertyId, ComplianceAlertKind.ConnectionInactive), "Villa <Rosa>");

        Assert.Equal("ISTAT in scadenza", approaching.Title);
        Assert.Equal("Villa <Rosa>: comunicazione ISTAT di ottobre 2026 da fare entro il 10/11/2026.", approaching.Body);
        Assert.Equal("Imposta di soggiorno scaduta", overdue.Title);
        Assert.Equal("Invio automatico non attivo", connection.Title);
        Assert.StartsWith("Villa <Rosa>: ", connection.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StayAlertKind.ReceiptMissing, "Ricevuta Alloggiati Web mancante")]
    [InlineData(StayAlertKind.ShortStayAtRisk, "Soggiorno breve da comunicare")]
    [InlineData(StayAlertKind.OverThirtyNights, "Soggiorno oltre 30 notti")]
    public void StayAlertPush_TheNewKinds_HaveTheirOwnPlainText(StayAlertKind kind, string italianTitle)
    {
        var italian = EmailTemplates.StayAlertPush(EmailTemplates.DefaultCulture, kind, "Villa <Rosa>", CheckIn);
        var english = EmailTemplates.StayAlertPush(CultureInfo.GetCultureInfo("en"), kind, "Villa <Rosa>", CheckIn);

        Assert.Equal(italianTitle, italian.Title);
        Assert.StartsWith("Villa <Rosa>: ", italian.Body, StringComparison.Ordinal);
        Assert.Contains("05/11/2026", italian.Body);
        Assert.NotEqual(italian.Title, english.Title);
        Assert.Contains("5 November 2026", english.Body);
    }

    [Fact]
    public void MonthLabel_AMonthIsWrittenInTheLanguageOfTheText_AndAnythingElseIsLeftAsItIs()
    {
        Assert.Equal("ottobre 2026", EmailTemplates.MonthLabel("2026-10", EmailTemplates.DefaultCulture));
        Assert.Equal("October 2026", EmailTemplates.MonthLabel("2026-10", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("2026-13", EmailTemplates.MonthLabel("2026-13", EmailTemplates.DefaultCulture));
        Assert.Equal("ottobre", EmailTemplates.MonthLabel("ottobre", EmailTemplates.DefaultCulture));
        Assert.Equal(string.Empty, EmailTemplates.MonthLabel(null, EmailTemplates.DefaultCulture));
    }

    [Fact]
    public void HostCompliance_IsTheCompliancePageOfTheShortRentArea()
    {
        Assert.Equal("https://casazen-app.test/app/short-rent/compliance", EmailTestHelpers.Links().HostCompliance());
    }

    private static EmailContent Render(string template, string cultureName, string value)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var period = (ComplianceAlertKind kind, ComplianceAlertType type, int reminder) =>
            EmailTemplates.CompliancePeriodAlert(
                culture,
                new ComplianceAlert(type, Guid.NewGuid(), kind, "2026-10", Deadline, reminder, reminder == 0 ? 0 : 3),
                value);
        var connection = (int reminder) =>
            EmailTemplates.ComplianceConnectionAlert(
                culture,
                new ComplianceAlert(ComplianceAlertType.ConnectionInactive, Guid.NewGuid(), ComplianceAlertKind.ConnectionInactive, ReminderNumber: reminder, MaxReminders: 1),
                value);
        return template switch
        {
            "receipt-missing" => EmailTemplates.ReceiptMissing(culture, value, CheckIn),
            "short-stay" => EmailTemplates.ShortStayAtRisk(culture, value, CheckIn),
            "short-stay-arrived" => EmailTemplates.ShortStayAtRisk(culture, value, CheckIn, new DateTime(2026, 11, 5, 19, 0, 0, DateTimeKind.Utc)),
            "over-thirty" => EmailTemplates.OverThirtyNights(culture, value, CheckIn),
            "istat-approaching" => period(ComplianceAlertKind.PeriodDeadlineApproaching, ComplianceAlertType.IstatPeriod, 0),
            "istat-overdue" => period(ComplianceAlertKind.PeriodOverdue, ComplianceAlertType.IstatPeriod, 0),
            "istat-reminder" => period(ComplianceAlertKind.PeriodOverdue, ComplianceAlertType.IstatPeriod, 2),
            "tax-approaching" => period(ComplianceAlertKind.PeriodDeadlineApproaching, ComplianceAlertType.TouristTaxPeriod, 0),
            "tax-overdue" => period(ComplianceAlertKind.PeriodOverdue, ComplianceAlertType.TouristTaxPeriod, 1),
            "connection" => connection(0),
            "connection-reminder" => connection(1),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }
}
