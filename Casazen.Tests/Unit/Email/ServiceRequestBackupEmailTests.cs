using System.Globalization;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// SP-BKP: the emails and pushes of the backup suppliers render in both languages with every argument in place, and the ones that go to a
/// supplier name the comune, never the property or the host's words (decision D9: the supplier learns them with the take).
/// </summary>
public class ServiceRequestBackupEmailTests
{
    private const string InboxUrl = "https://app.casazen.test/app/supplier/inbox";
    private const string PropertyName = "Casa Riservata";
    private static readonly DateTime Due = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryEmailToABackup_RendersWithTheComune_AndWithoutAPlaceholderLeft(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var emails = new List<EmailContent>
        {
            EmailTemplates.ServiceRequestBackupInvited(culture, "Alfa Srl", ServiceCategories.Cleaning, "Bari", InboxUrl, Due, Due.AddHours(4), 6000),
            EmailTemplates.ServiceRequestBackupActive(culture, "Alfa Srl", ServiceCategories.Cleaning, "Bari", InboxUrl, Due),
            EmailTemplates.ServiceRequestBackupWon(culture, "Alfa Srl", ServiceCategories.Cleaning, "Bari", InboxUrl, Due.AddHours(4), 6000),
        };
        emails.AddRange(Enum.GetValues<ServiceRequestBackupClosedKind>().Select(kind =>
            EmailTemplates.ServiceRequestBackupClosed(culture, "Alfa Srl", ServiceCategories.Cleaning, "Bari", kind, InboxUrl)));

        Assert.All(emails, email =>
        {
            Assert.False(string.IsNullOrWhiteSpace(email.Subject));
            Assert.Contains("Bari", email.Subject + email.HtmlBody);
            Assert.Contains(InboxUrl, email.HtmlBody);
            Assert.DoesNotContain("{0}", email.Subject + email.HtmlBody);
            Assert.DoesNotContain("{1}", email.Subject + email.HtmlBody);
            Assert.DoesNotContain("{2}", email.Subject + email.HtmlBody);
            Assert.DoesNotContain(PropertyName, email.Subject + email.HtmlBody);
        });
    }

    [Fact]
    public void TheInvitation_SaysPlainlyThatTheSupplierIsABackupAndUntilWhen_InBothLanguages()
    {
        var italian = EmailTemplates.ServiceRequestBackupInvited(
            CultureInfo.GetCultureInfo("it-IT"), "Alfa Srl", ServiceCategories.Cleaning, "Bari", InboxUrl, Due);
        var english = EmailTemplates.ServiceRequestBackupInvited(
            CultureInfo.GetCultureInfo("en"), "Alfa Srl", ServiceCategories.Cleaning, "Bari", InboxUrl, Due);

        // 30/10/2026 11:00 in Rome (summer time ended on the 25th: winter time, UTC+1).
        Assert.Contains("fornitore di riserva", italian.HtmlBody);
        Assert.Contains("solo se quel fornitore non accetta", italian.HtmlBody);
        Assert.Contains("30/10/2026 11:00", italian.HtmlBody);
        Assert.Contains("backup supplier", english.HtmlBody);
        Assert.Contains("only if that supplier does not accept", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void TheEmailToTheHostWhoseJobABackupTook_NamesTheNewSupplier_AndTheLinkWhenTheRequestHasAStay(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);

        var withStay = EmailTemplates.ServiceRequestTakenByBackup(
            culture, ServiceCategories.Cleaning, PropertyName, "Beta Srl", Due, "https://app.casazen.test/app/bookings/1");
        var withoutStay = EmailTemplates.ServiceRequestTakenByBackup(culture, ServiceCategories.Cleaning, PropertyName, "Beta Srl", null, null);

        Assert.Contains("Beta Srl", withStay.HtmlBody);
        Assert.Contains(PropertyName, withStay.Subject);
        Assert.Contains("https://app.casazen.test/app/bookings/1", withStay.HtmlBody);
        Assert.DoesNotContain("href=", withoutStay.HtmlBody);
        Assert.DoesNotContain("{2}", withStay.HtmlBody);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryPushOfTheBackups_HasATitleAndABody_WithNoPlaceholderLeft(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var pushes = new[]
        {
            EmailTemplates.ServiceRequestBackupInvitedPush(culture, ServiceCategories.Cleaning, "Bari"),
            EmailTemplates.ServiceRequestBackupActivePush(culture, ServiceCategories.Cleaning, "Bari"),
            EmailTemplates.ServiceRequestBackupWonPush(culture, ServiceCategories.Cleaning, "Bari"),
            EmailTemplates.ServiceRequestBackupClosedPush(culture, ServiceCategories.Cleaning, "Bari"),
            EmailTemplates.ServiceRequestTakenByBackupPush(culture, ServiceCategories.Cleaning, PropertyName, "Beta Srl"),
        };

        Assert.All(pushes, push =>
        {
            Assert.False(string.IsNullOrWhiteSpace(push.Title));
            Assert.False(string.IsNullOrWhiteSpace(push.Body));
            Assert.DoesNotContain("{", push.Title + push.Body);
        });
        // A push to a supplier carries the comune and never the property.
        Assert.All(pushes.Take(4), push => Assert.DoesNotContain(PropertyName, push.Title + push.Body));
        Assert.All(pushes.Take(4), push => Assert.Contains("Bari", push.Body));
    }
}
