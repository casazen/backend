using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>CO-10: texts of the stay alerts (Alloggiati stages, failed communication, check-out reminder), email and push.</summary>
public class StayAlertEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private static readonly DateTime CheckIn = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = new(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Templates => new()
    {
        "data-missing", "approaching", "approaching-arrived", "overdue", "overdue-reminder", "failed", "checkout",
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Render_PayloadInNames_IsHtmlEncoded(string template)
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
    public void GuestCheckInIncomplete_Italian_IsAboutMissingGuestDataForAlloggiati()
    {
        var content = EmailTemplates.GuestCheckInIncomplete(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn);

        Assert.Equal("Dati ospiti mancanti — Villa Rosa (05/10/2026)", content.Subject);
        Assert.Contains("Alloggiati Web", content.HtmlBody);
    }

    [Fact]
    public void AlloggiatiDeadline_ShortStayWithRegisteredArrival_StatesSixHoursAndTheDeadlineInItalianTime()
    {
        var content = EmailTemplates.AlloggiatiDeadline(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn, shortStay: true, Deadline);

        Assert.Contains("entro 6 ore dall'arrivo", content.HtmlBody);
        Assert.Contains("Scadenza: <strong>05/10/2026 21:00</strong>", content.HtmlBody);
        Assert.DoesNotContain("non è registrato", content.HtmlBody);
    }

    [Fact]
    public void AlloggiatiDeadline_ArrivalNotRegistered_StatesTheTermAndAsksToRegisterTheArrival()
    {
        var content = EmailTemplates.AlloggiatiDeadline(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn);

        Assert.Contains("entro 24 ore dall'arrivo", content.HtmlBody);
        Assert.Contains("registra l'arrivo", content.HtmlBody);
        Assert.DoesNotContain("Scadenza:", content.HtmlBody);
        Assert.Contains("Segna come inviato manualmente", content.HtmlBody);
    }

    [Fact]
    public void AlloggiatiOverdue_FirstAlertAndReminder_OnlyTheReminderShowsItsNumber()
    {
        var first = EmailTemplates.AlloggiatiOverdue(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn, null);
        var reminder = EmailTemplates.AlloggiatiOverdue(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn, null, 1, 2);

        Assert.Equal("Alloggiati Web scaduta - Villa Rosa (05/10/2026)", first.Subject);
        Assert.DoesNotContain("Promemoria", first.HtmlBody);
        Assert.Contains("Promemoria 1 di 2.", reminder.HtmlBody);
        Assert.Contains("fine del giorno di arrivo", reminder.HtmlBody);
        Assert.Contains("non riceverai altri promemoria", reminder.HtmlBody);
    }

    [Fact]
    public void AlloggiatiFailed_Italian_IsNotTheIncompleteCheckInText()
    {
        var content = EmailTemplates.AlloggiatiFailed(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn);

        Assert.Equal("Invio Alloggiati Web non riuscito - Villa Rosa (05/10/2026)", content.Subject);
        Assert.DoesNotContain("Check-in incompleto", content.Subject + content.HtmlBody);
        Assert.DoesNotContain("mancano ancora dati", content.HtmlBody);
    }

    [Fact]
    public void CheckoutReminder_ItalianAndEnglish_ShowCheckOutDayAndProperty()
    {
        var italian = EmailTemplates.CheckoutReminder(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn.AddDays(3));
        var english = EmailTemplates.CheckoutReminder(CultureInfo.GetCultureInfo("en"), "Anna", "Villa Rosa", CheckIn.AddDays(3));

        Assert.Equal("Check-out di oggi - Villa Rosa (08/10/2026)", italian.Subject);
        Assert.Contains("<strong>08/10/2026</strong>", italian.HtmlBody);
        Assert.Equal("Today's check-out - Villa Rosa (8 October 2026)", english.Subject);
    }

    [Theory]
    [InlineData(StayAlertKind.GuestDataMissing, "Dati ospiti mancanti")]
    [InlineData(StayAlertKind.AlloggiatiDeadlineApproaching, "Alloggiati Web in scadenza")]
    [InlineData(StayAlertKind.AlloggiatiOverdue, "Alloggiati Web scaduta")]
    [InlineData(StayAlertKind.AlloggiatiFailed, "Invio Alloggiati Web non riuscito")]
    public void StayAlertPush_AlloggiatiKinds_HaveTheirOwnPlainText(StayAlertKind kind, string italianTitle)
    {
        var italian = EmailTemplates.StayAlertPush(EmailTemplates.DefaultCulture, kind, "Villa <Rosa>", CheckIn);
        var english = EmailTemplates.StayAlertPush(CultureInfo.GetCultureInfo("en"), kind, "Villa <Rosa>", CheckIn);

        Assert.Equal(italianTitle, italian.Title);
        Assert.StartsWith("Villa <Rosa>: ", italian.Body, StringComparison.Ordinal);
        Assert.Contains("05/10/2026", italian.Body);
        Assert.NotEqual(italian.Title, english.Title);
        Assert.Contains("5 October 2026", english.Body);
    }

    [Fact]
    public void StayAlertPush_CheckoutReminder_HasNoTextOfItsOwn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmailTemplates.StayAlertPush(EmailTemplates.DefaultCulture, StayAlertKind.CheckoutReminder, "Villa Rosa", CheckIn));
    }

    private static EmailContent Render(string template, string cultureName, string value)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        return template switch
        {
            "data-missing" => EmailTemplates.GuestCheckInIncomplete(culture, value, value, CheckIn),
            "approaching" => EmailTemplates.AlloggiatiDeadline(culture, value, value, CheckIn),
            "approaching-arrived" => EmailTemplates.AlloggiatiDeadline(culture, value, value, CheckIn, shortStay: true, Deadline),
            "overdue" => EmailTemplates.AlloggiatiOverdue(culture, value, value, CheckIn, Deadline),
            "overdue-reminder" => EmailTemplates.AlloggiatiOverdue(culture, value, value, CheckIn, null, 2, 2),
            "failed" => EmailTemplates.AlloggiatiFailed(culture, value, value, CheckIn),
            "checkout" => EmailTemplates.CheckoutReminder(culture, value, value, CheckIn.AddDays(3)),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }
}
