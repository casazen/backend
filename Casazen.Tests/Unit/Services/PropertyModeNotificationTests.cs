using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: the e-mails to the host about a scheduled change of rental mode (programmed, applied, failed): Italian by default
/// and English, template names for the logs, a link to the property page of the right area, every dynamic value
/// HTML-encoded, queued to the contact address of the org and never thrown from the notifier.
/// </summary>
public class PropertyModeNotificationTests : IDisposable
{
    private const string BaseUrl = EmailTestHelpers.PublicSiteBaseUrl;

    private readonly PropertyModeHarness _h = new();
    private readonly RecordingEmailQueue _emails = new();

    public void Dispose() => _h.Dispose();

    // ─── NotificationService ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scheduled_ToLong_IsQueuedToTheContactAddressWithTheDateAndALinkToTheShortRentProperty()
    {
        var property = await _h.SeedPropertyAsync(name: "Casa del Mare", contactEmail: "host@casadelmare.test");
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));

        var queued = await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled));

        Assert.True(queued);
        var (to, content, template) = Assert.Single(_emails.Snapshot());
        Assert.Equal("host@casadelmare.test", to);
        Assert.Equal(EmailTemplates.Names.PropertyModeChangeScheduled, template);
        Assert.Equal("property-mode-change-scheduled", template);
        Assert.Equal("Passaggio ad affitto lungo programmato - Casa del Mare", content.Subject);
        Assert.Contains("dagli affitti brevi agli affitti lunghi, a partire dal <strong>20/10/2026</strong>", content.HtmlBody);
        Assert.Contains($"href=\"{BaseUrl}/app/short-rent/properties/{property.Id:D}\"", content.HtmlBody);
    }

    [Fact]
    public async Task Scheduled_ToShort_LinksTheLongRentPropertyPage()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Day(20));

        await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled));

        var (_, content, _) = Assert.Single(_emails.Snapshot());
        Assert.Contains("Passaggio ad affitto breve programmato", content.Subject);
        Assert.Contains($"href=\"{BaseUrl}/app/long-rent/properties/{property.Id:D}\"", content.HtmlBody);
    }

    [Fact]
    public async Task Applied_ToLong_SaysUntilWhenTheDatesStayClosedAndLinksTheLongRentProperty()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20), PropertyModeChangeStatus.Applied);

        await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Applied));

        var (_, content, template) = Assert.Single(_emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.PropertyModeChangeApplied, template);
        Assert.Equal("Casa del Mare è ora in affitto lungo", content.Subject);
        Assert.Contains("restano chiuse nel calendario fino al 20/10/2028", content.HtmlBody);
        Assert.Contains("rimuovi l'annuncio dal portale", content.HtmlBody);
        Assert.Contains($"href=\"{BaseUrl}/app/long-rent/properties/{property.Id:D}\"", content.HtmlBody);
    }

    [Fact]
    public async Task Applied_ToShort_LinksTheActivationWizard()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Day(20), PropertyModeChangeStatus.Applied);

        await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Applied));

        var (_, content, _) = Assert.Single(_emails.Snapshot());
        Assert.Equal("Casa del Mare è tornato in affitto breve", content.Subject);
        Assert.Contains($"href=\"{BaseUrl}/app/short-rent/properties/{property.Id:D}/activation\"", content.HtmlBody);
        Assert.Contains("Controlla i requisiti", content.HtmlBody);
    }

    [Fact]
    public async Task Failed_ToLongBlockedByStays_SaysWhyTheFirstFreeDayAndThatTheCalendarIsOpenAgain()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(15), PropertyModeChangeStatus.Failed);
        await SetFailureAsync(change, PropertyModeErrorCodes.BlockedByBookings);

        await NewNotifier().SendPropertyModeChangeAsync(
            new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Failed, EarliestDate: Day(21)));

        var (_, content, template) = Assert.Single(_emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.PropertyModeChangeFailed, template);
        Assert.Equal("Passaggio non riuscito - Casa del Mare", content.Subject);
        Assert.Contains("ad affitto lungo previsto per il <strong>15/10/2026</strong>: l'immobile è rimasto in affitto breve", content.HtmlBody);
        Assert.Contains("soggiorni o prenotazioni dei portali", content.HtmlBody);
        Assert.Contains("sono di nuovo aperte nel calendario", content.HtmlBody);
        Assert.Contains("Il primo giorno possibile adesso è il <strong>21/10/2026</strong>", content.HtmlBody);
        Assert.Contains($"href=\"{BaseUrl}/app/short-rent/properties/{property.Id:D}\"", content.HtmlBody);
    }

    [Fact]
    public async Task Failed_ToShortBlockedByALeaseWithoutADay_SuggestsToTryAgain()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Day(15), PropertyModeChangeStatus.Failed);
        await SetFailureAsync(change, PropertyModeErrorCodes.BlockedByLease);

        await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Failed));

        var (_, content, _) = Assert.Single(_emails.Snapshot());
        Assert.Contains("contratto di locazione che termina dopo quel giorno", content.HtmlBody);
        Assert.Contains("Puoi programmare di nuovo il passaggio", content.HtmlBody);
        Assert.DoesNotContain("sono di nuovo aperte nel calendario", content.HtmlBody);
        Assert.Contains($"href=\"{BaseUrl}/app/long-rent/properties/{property.Id:D}\"", content.HtmlBody);
    }

    [Fact]
    public async Task PropertyNameWithMarkup_IsHtmlEncoded()
    {
        const string payload = "<a href=\"https://phish.example\">Villa</a>";
        var property = await _h.SeedPropertyAsync(name: payload);
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));

        await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled));

        var (_, content, _) = Assert.Single(_emails.Snapshot());
        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Villa&lt;/a&gt;", content.HtmlBody);
    }

    [Fact]
    public async Task UnknownChange_QueuesNothingAndReturnsFalse()
    {
        var queued = await NewNotifier().SendPropertyModeChangeAsync(
            new PropertyModeNotice(Guid.NewGuid(), PropertyModeNoticeKind.Scheduled));

        Assert.False(queued);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task ChangeOfADeletedProperty_QueuesNothing()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));
        var row = await _h.Db.Properties.FindAsync(property.Id);
        row!.IsDeleted = true;
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();

        var queued = await NewNotifier().SendPropertyModeChangeAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Applied));

        Assert.False(queued);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task EmailNotQueued_ReturnsFalseWithoutThrowing()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(false);

        var queued = await NewNotifier(queue.Object).SendPropertyModeChangeAsync(
            new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled));

        Assert.False(queued);
    }

    [Fact]
    public async Task LinksNotConfigured_TheEmailHasNoButton()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));

        await NewNotifier(links: EmailTestHelpers.Links(baseUrl: null)).SendPropertyModeChangeAsync(
            new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled));

        var (_, content, _) = Assert.Single(_emails.Snapshot());
        Assert.DoesNotContain("<a href", content.HtmlBody);
    }

    [Fact]
    public async Task UnknownKind_Throws()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(20));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewNotifier().SendPropertyModeChangeAsync(
            new PropertyModeNotice(change.Id, (PropertyModeNoticeKind)9)));
    }

    // ─── Templates: Italian and English ─────────────────────────────────────────────────────────────

    public static TheoryData<string> Cultures => new() { "it-IT", "en" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Templates_EveryVariant_ProduceCompleteDocumentsWithoutPlaceholders(string culture)
    {
        var info = CultureInfo.GetCultureInfo(culture);
        var documents = new[]
        {
            EmailTemplates.PropertyModeChangeScheduled(info, "Villa Rosa", RentalMode.Long, Day(20), $"{BaseUrl}/app/short-rent/properties/1"),
            EmailTemplates.PropertyModeChangeScheduled(info, "Villa Rosa", RentalMode.Short, Day(20), $"{BaseUrl}/app/long-rent/properties/1"),
            EmailTemplates.PropertyModeChangeApplied(info, "Villa Rosa", RentalMode.Long, Day(20), $"{BaseUrl}/app/long-rent/properties/1"),
            EmailTemplates.PropertyModeChangeApplied(info, "Villa Rosa", RentalMode.Long, null, null),
            EmailTemplates.PropertyModeChangeApplied(info, "Villa Rosa", RentalMode.Short, null, $"{BaseUrl}/app/short-rent/properties/1/activation"),
            EmailTemplates.PropertyModeChangeFailed(info, "Villa Rosa", RentalMode.Long, Day(15), PropertyModeErrorCodes.BlockedByBookings, Day(21), $"{BaseUrl}/p"),
            EmailTemplates.PropertyModeChangeFailed(info, "Villa Rosa", RentalMode.Short, Day(15), PropertyModeErrorCodes.BlockedByLease, null, $"{BaseUrl}/p"),
            EmailTemplates.PropertyModeChangeFailed(info, "Villa Rosa", RentalMode.Short, Day(15), PropertyModeErrorCodes.BlockedByDraftLease, null, null),
            EmailTemplates.PropertyModeChangeFailed(info, "Villa Rosa", RentalMode.Long, Day(15), PropertyModeErrorCodes.PropertyChanged, null, null),
            EmailTemplates.PropertyModeChangeFailed(info, "Villa Rosa", RentalMode.Long, Day(15), null, null, null),
        };

        foreach (var content in documents)
        {
            Assert.StartsWith("<!DOCTYPE html>", content.HtmlBody);
            Assert.Contains($"<html lang=\"{info.TwoLetterISOLanguageName}\">", content.HtmlBody);
            Assert.Contains("Villa Rosa", content.Subject);
            Assert.False(string.IsNullOrWhiteSpace(content.Subject));
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
        }

        // Five different subjects for the five situations (programmed to either mode, applied to either, failed), not one text.
        Assert.Equal(5, documents.Select(d => d.Subject).Distinct().Count());
    }

    [Fact]
    public void Templates_English_AreTheEnglishTexts()
    {
        var english = CultureInfo.GetCultureInfo("en");

        var scheduled = EmailTemplates.PropertyModeChangeScheduled(english, "Villa Rosa", RentalMode.Long, Day(20), $"{BaseUrl}/p");
        var failed = EmailTemplates.PropertyModeChangeFailed(
            english, "Villa Rosa", RentalMode.Short, Day(15), PropertyModeErrorCodes.BlockedByLease, Day(62), $"{BaseUrl}/p");

        Assert.Equal("Switch to long-term rental scheduled - Villa Rosa", scheduled.Subject);
        Assert.Contains("from short stays to long-term rental, starting on <strong>20 October 2026</strong>", scheduled.HtmlBody);
        Assert.Contains("Open the property", scheduled.HtmlBody);
        Assert.Equal("Switch not applied - Villa Rosa", failed.Subject);
        Assert.Contains("to short-stay rental planned for <strong>15 October 2026</strong>: the property is still in long-term rental mode", failed.HtmlBody);
        Assert.Contains("The first possible day now is <strong>1 December 2026</strong>", failed.HtmlBody);
    }

    [Fact]
    public void Failed_TheReasonsAreTheTextOfTheirCode()
    {
        var it = EmailTemplates.DefaultCulture;

        string Body(string? reason) => EmailTemplates.PropertyModeChangeFailed(it, "Villa", RentalMode.Short, Day(15), reason).HtmlBody;

        Assert.Contains("soggiorni o prenotazioni dei portali", Body(PropertyModeErrorCodes.BlockedByBookings));
        Assert.Contains("contratto di locazione che termina", Body(PropertyModeErrorCodes.BlockedByLease));
        Assert.Contains("bozza di contratto", Body(PropertyModeErrorCodes.BlockedByDraftLease));
        Assert.Contains("L'immobile è cambiato dopo la programmazione", Body(PropertyModeErrorCodes.PropertyChanged));
        Assert.Contains("L'immobile è cambiato dopo la programmazione", Body(null));
    }

    [Fact]
    public void Links_HostProperty_IsThePageOfTheAreaOfTheMode()
    {
        var links = EmailTestHelpers.Links();
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal($"{BaseUrl}/app/short-rent/properties/{id:D}", links.HostProperty(id, RentalMode.Short));
        Assert.Equal($"{BaseUrl}/app/long-rent/properties/{id:D}", links.HostProperty(id, RentalMode.Long));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private NotificationService NewNotifier(IEmailQueue? queue = null, PublicSiteLinks? links = null) =>
        new(
            _h.Db,
            queue ?? _emails,
            Mock.Of<IPushNotificationService>(),
            links ?? EmailTestHelpers.Links(),
            NullLogger<NotificationService>.Instance);

    private async Task SetFailureAsync(PropertyModeChange change, string reason)
    {
        _h.Db.ChangeTracker.Clear();
        var row = await _h.Db.PropertyModeChanges.FindAsync(change.Id);
        row!.FailureReason = reason;
        row.FailedAt = Now.UtcDateTime;
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();
    }
}
