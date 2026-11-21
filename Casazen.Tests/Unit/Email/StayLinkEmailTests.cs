using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.GuestPortal;
using Casazen.Tests.Unit.Push;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E50/F01/US03/T03 (NF-152, section 5.1): the stay link of the guest portal travels in the e-mails to the guest. The confirmation of a booking carries it as its first button, and
/// «Prepara il tuo arrivo» takes the place of the e-mail of the check-in link (same job, same timing, same delivery tracking, the check-in link as the second button), only while
/// <c>Features:GuestPortal</c> is on; with it off both are what they have always been, to the byte. The address is made by the link service of the portal and handed to the template: a
/// template never builds it.
/// </summary>
public sealed class StayLinkEmailTests : IDisposable
{
    private readonly GuestPortalTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    /// <summary>The e-mail address of the guest of the test world's stay.</summary>
    private string GuestEmail => _world.Db.Guests.AsNoTracking().Single().Email;

    // ---- The confirmation ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST (data: what the guest is given): with the portal on, the confirmation of a booking carries the stay link of the booking, made by the link service, as its first button with
    /// its fallback link, and the rest of the e-mail is still there; with it off the confirmation is identical to the one a notifier with none of the portal's services writes, no link is
    /// made, and nothing of the portal is in it.
    /// </summary>
    [Fact]
    public async Task Confirmation_FlagOn_CarriesStayLink_FlagOff_Identical()
    {
        await MakeDirectBookingAsync();

        // The confirmation of today: a notifier built with none of the services of the portal.
        var baseline = new RecordingEmailQueue();
        await new BookingNotifier(_world.Db, baseline, _world.SiteLinks, new RecordingPushQueue(), NullLogger<BookingNotifier>.Instance)
            .BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);
        var today = ConfirmationOf(baseline);
        Assert.DoesNotContain("/s/", today.HtmlBody, StringComparison.Ordinal);

        // Portal off, every service of the portal wired: the same e-mail, no sender name, no reply-to, and no link made.
        var flags = new TestFeatureFlags();
        var off = new RecordingEmailQueue();
        await Notifier(off, flags).BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);
        var disabled = ConfirmationOf(off);
        Assert.Equal(today.Subject, disabled.Subject);
        Assert.Equal(today.HtmlBody, disabled.HtmlBody);
        Assert.Null(disabled.FromName);
        Assert.Null(disabled.ReplyTo);
        Assert.Null(await _world.LinkService().GetActiveAsync(_world.Org.Id, _world.Booking.Id));

        // Portal on: the stay link, the first button, with its fallback; the notice of the check-in and the button of «Le mie prenotazioni» stay.
        flags.Set(FeatureFlags.GuestPortal, true);
        var on = new RecordingEmailQueue();
        await Notifier(on, flags).BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);
        var link = await _world.LinkService().GetActiveAsync(_world.Org.Id, _world.Booking.Id);
        Assert.NotNull(link);
        var url = _world.LinkService().BuildUrl(link);
        Assert.StartsWith("https://app.casazen.example/s/", url, StringComparison.Ordinal);

        var html = ConfirmationOf(on).HtmlBody;
        Assert.Contains($"<a href=\"{url}\" style=", html, StringComparison.Ordinal);
        Assert.Contains("Apri il tuo soggiorno</a>", html, StringComparison.Ordinal);
        Assert.Contains($"copia questo link nel browser:<br /><a href=\"{url}\">{url}</a>", html, StringComparison.Ordinal);
        Assert.Contains("Le mie prenotazioni</a>", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf(url, StringComparison.Ordinal) < html.IndexOf("Le mie prenotazioni</a>", StringComparison.Ordinal), "the stay link is the first button");
        Assert.Single(on.Snapshot(), e => e.To == GuestEmail);

        // Asked again, the same link: the link is made once.
        await Notifier(new RecordingEmailQueue(), flags).BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);
        Assert.Equal(1, await _world.Db.GuestPortalLinks.AsNoTracking().CountAsync(l => l.BookingId == _world.Booking.Id));
    }

    /// <summary>A booking that may not have a stay link (a guest who did not show up) gets the confirmation it always got, with the portal on.</summary>
    [Fact]
    public async Task Confirmation_FlagOn_BookingThatMayNotHaveALink_GetsTheEmailOfToday()
    {
        await MakeDirectBookingAsync(noShow: true);
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var queue = new RecordingEmailQueue();

        await Notifier(queue, flags).BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);

        Assert.DoesNotContain("/s/", ConfirmationOf(queue).HtmlBody, StringComparison.Ordinal);
        Assert.Null(await _world.LinkService().GetActiveAsync(_world.Org.Id, _world.Booking.Id));
    }

    /// <summary>With the host's account in German, the confirmation with the stay link is written in German and sent in the host's name.</summary>
    [Fact]
    public async Task Confirmation_FlagOn_IsWrittenInTheLanguageOfTheHostAccountAndSentInItsName()
    {
        await MakeDirectBookingAsync();
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var queue = new RecordingEmailQueue();

        await Notifier(queue, flags, withBrandAndLanguage: true).BookingConfirmedAsync(_world.Booking.Id, BookingConfirmationKind.PaidOnline);

        var content = ConfirmationOf(queue);
        Assert.Contains("<html lang=\"de\">", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ihren Aufenthalt öffnen</a>", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Buchung bestätigt", content.Subject, StringComparison.Ordinal);
        Assert.Equal("Casa Vacanze Aurora", content.FromName);
        Assert.Equal(_world.Org.ContactEmail, content.ReplyTo);
    }

    // ---- «Prepara il tuo arrivo» ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST (data: the guest still reaches the check-in, and the host still sees whether it left): with the portal on the e-mail the daily job sends takes the place of the check-in e-mail
    /// («Prepara il tuo arrivo»: the stay link first, the check-in link second), and what the job writes on the session is what it has always written (<c>Sent</c>, when, no error): the
    /// delivery tracking of the check-in link is the same. With the portal off the job sends the e-mail of the check-in link as ever, and the reminder of the missing data is never replaced.
    /// </summary>
    [Fact]
    public async Task PrepareArrival_FlagOn_KeepsCheckInLinkAndTracking()
    {
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var issued = await IssueCheckInLinkAsync();
        var email = new CapturingEmailService();

        await Job(email, flags).SendAsync(issued.SessionId, issued.Token, 1);

        var sent = Assert.Single(email.Sent);
        Assert.Equal(GuestEmail, sent.To);
        Assert.Equal("Prepara il tuo arrivo — Trullo Aurora", sent.Subject);
        var html = sent.Html;
        Assert.Contains("<h1 style=\"font-size:20px;\">Prepara il tuo arrivo</h1>", html, StringComparison.Ordinal);

        var link = await _world.LinkService().GetActiveAsync(_world.Org.Id, _world.Booking.Id);
        var stayUrl = _world.LinkService().BuildUrl(link!);
        var checkInUrl = $"https://app.casazen.example/checkin/{issued.Token}";
        Assert.Contains($"<a href=\"{stayUrl}\" style=", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{checkInUrl}\" style=", html, StringComparison.Ordinal);
        Assert.Contains("Apri il tuo soggiorno</a>", html, StringComparison.Ordinal);
        Assert.Contains("Completa il check-in</a>", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf(stayUrl, StringComparison.Ordinal) < html.IndexOf(checkInUrl, StringComparison.Ordinal), "the stay link is the main button, the check-in link the second");
        Assert.Contains("Il link è valido fino al", html, StringComparison.Ordinal);

        // The delivery tracking is the one of the e-mail of the link.
        var session = await _world.Db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.Id == issued.SessionId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Sent, session.LinkEmailStatus);
        Assert.Null(session.LinkEmailError);
        Assert.Equal(GuestPortalTestWorld.Now.UtcDateTime, session.SentAt);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);

        // The token opens the check-in as before: the link of the e-mail is the one of the session.
        Assert.Equal(GuestCheckInService.HashToken(issued.Token), session.TokenHash);

        // A provider that refuses is retried by the job as it always was: the e-mail carried the links, the tracking says so.
        var failing = new CapturingEmailService(EmailSendResult.NotConfigured());
        var second = await IssueCheckInLinkAsync();
        await Job(failing, flags).SendAsync(second.SessionId, second.Token, 1);
        var failed = await _world.Db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.Id == second.SessionId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, failed.LinkEmailStatus);
    }

    [Fact]
    public async Task PrepareArrival_FlagOff_IsTheCheckInEmailOfToday()
    {
        var issued = await IssueCheckInLinkAsync();
        var off = new CapturingEmailService();
        await Job(off, new TestFeatureFlags()).SendAsync(issued.SessionId, issued.Token, 1);

        // The job built with none of the services of the portal is the baseline.
        var second = await IssueCheckInLinkAsync();
        var baseline = new CapturingEmailService();
        await PlainJob(baseline).SendAsync(second.SessionId, second.Token, 1);

        var disabled = Assert.Single(off.Sent);
        var today = Assert.Single(baseline.Sent);
        Assert.Equal("Completa il check-in per il tuo soggiorno — Trullo Aurora", disabled.Subject);
        Assert.Equal(today.Subject, disabled.Subject);
        Assert.Equal(today.Html.Replace(second.Token, "TOKEN", StringComparison.Ordinal), disabled.Html.Replace(issued.Token, "TOKEN", StringComparison.Ordinal));
        Assert.DoesNotContain("/s/", disabled.Html, StringComparison.Ordinal);
        Assert.Empty(await _world.Db.GuestPortalLinks.AsNoTracking().ToListAsync());
    }

    /// <summary>The reminder of the missing data keeps its own e-mail, and a booking that may not have a stay link gets the check-in e-mail, with the portal on.</summary>
    [Fact]
    public async Task PrepareArrival_FlagOn_ReminderIsNotReplaced_AndABookingWithoutALinkGetsTheCheckInEmail()
    {
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);

        var reminder = await IssueCheckInLinkAsync(reminder: true);
        var reminderMail = new CapturingEmailService();
        await Job(reminderMail, flags).SendAsync(reminder.SessionId, reminder.Token, 1);
        var sentReminder = Assert.Single(reminderMail.Sent);
        Assert.StartsWith("Promemoria: completa il check-in", sentReminder.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("/s/", sentReminder.Html, StringComparison.Ordinal);

        var noShow = await _world.Db.Bookings.SingleAsync(b => b.Id == _world.Booking.Id);
        noShow.NoShowAt = GuestPortalTestWorld.Now.UtcDateTime;
        await _world.Db.SaveChangesAsync();
        var issued = await IssueCheckInLinkAsync();
        var mail = new CapturingEmailService();
        await Job(mail, flags).SendAsync(issued.SessionId, issued.Token, 1);

        var sent = Assert.Single(mail.Sent);
        Assert.StartsWith("Completa il check-in per il tuo soggiorno", sent.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("/s/", sent.Html, StringComparison.Ordinal);
    }

    /// <summary>The e-mail goes in the name of the host, with its brand, in the language of the host's account, and still carries both links.</summary>
    [Fact]
    public async Task PrepareArrival_FlagOn_InTheHostsNameAndLanguage()
    {
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var issued = await IssueCheckInLinkAsync();
        var email = new CapturingEmailService();

        await Job(email, flags, withBrandAndLanguage: true).SendAsync(issued.SessionId, issued.Token, 1);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("Casa Vacanze Aurora", sent.FromName);
        Assert.Equal(_world.Org.ContactEmail, sent.ReplyTo);
        Assert.Equal("Bereiten Sie Ihre Anreise vor — Trullo Aurora", sent.Subject);
        Assert.Contains("<html lang=\"de\">", sent.Html, StringComparison.Ordinal);
        Assert.Contains("Ihren Aufenthalt öffnen</a>", sent.Html, StringComparison.Ordinal);
        Assert.Contains("Check-in abschließen</a>", sent.Html, StringComparison.Ordinal);
        Assert.Contains("background:#336699", sent.Html, StringComparison.Ordinal);
        Assert.Contains("Powered by CasaZen", sent.Html, StringComparison.Ordinal);
    }

    // ---- The stay link provider ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StayLinkProvider_FlagOff_GivesNoAddress_AndMakesNoLink()
    {
        var provider = Provider(new TestFeatureFlags());

        Assert.Null(await provider.UrlAsync(_world.Org.Id, _world.Booking.Id));
        Assert.Empty(await _world.Db.GuestPortalLinks.AsNoTracking().ToListAsync());

        IGuestStayLinkProvider? none = null;
        Assert.Null(await none.UrlOrNullAsync(_world.Org.Id, _world.Booking.Id));
    }

    [Fact]
    public async Task StayLinkProvider_FlagOn_GivesTheAddressOfTheOneActiveLink_OfThatOrgOnly()
    {
        var provider = Provider(new TestFeatureFlags(FeatureFlags.GuestPortal));

        var first = await provider.UrlAsync(_world.Org.Id, _world.Booking.Id);
        var again = await provider.UrlAsync(_world.Org.Id, _world.Booking.Id);

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.Equal(first, _world.LinkService().BuildUrl((await _world.LinkService().GetActiveAsync(_world.Org.Id, _world.Booking.Id))!));

        // A booking that is not the org's gives no link, and none is made.
        var foreign = await provider.UrlAsync(Guid.NewGuid(), _world.Booking.Id);
        Assert.Null(foreign);
        Assert.Equal(1, await _world.Db.GuestPortalLinks.AsNoTracking().CountAsync());
    }

    // ---- The data every test runs on ---------------------------------------------------------------------------------------------------

    private BookingNotifier Notifier(RecordingEmailQueue queue, TestFeatureFlags flags, bool withBrandAndLanguage = false) =>
        new(
            _world.Db,
            queue,
            _world.SiteLinks,
            new RecordingPushQueue(),
            NullLogger<BookingNotifier>.Instance,
            emailBranding: withBrandAndLanguage ? new GuestEmailBrandingProvider(_world.Db, flags, NullLogger<GuestEmailBrandingProvider>.Instance) : null,
            emailLanguage: withBrandAndLanguage ? new GuestEmailLanguageProvider(_world.Db, flags, NullLogger<GuestEmailLanguageProvider>.Instance) : null,
            stayLinks: Provider(flags));

    private GuestCheckInLinkEmailJob Job(CapturingEmailService email, TestFeatureFlags flags, bool withBrandAndLanguage = false) =>
        new(
            _world.Db,
            email,
            _world.SiteLinks,
            Mock.Of<IBackgroundJobClient>(),
            NullLogger<GuestCheckInLinkEmailJob>.Instance,
            _world.Clock,
            emailBranding: withBrandAndLanguage ? new GuestEmailBrandingProvider(_world.Db, flags, NullLogger<GuestEmailBrandingProvider>.Instance) : null,
            emailLanguage: withBrandAndLanguage ? new GuestEmailLanguageProvider(_world.Db, flags, NullLogger<GuestEmailLanguageProvider>.Instance) : null,
            stayLinks: Provider(flags));

    /// <summary>The job built with none of the services of the portal: the e-mail of the check-in link of today.</summary>
    private GuestCheckInLinkEmailJob PlainJob(CapturingEmailService email) =>
        new(_world.Db, email, _world.SiteLinks, Mock.Of<IBackgroundJobClient>(), NullLogger<GuestCheckInLinkEmailJob>.Instance, _world.Clock);

    private GuestStayLinkProvider Provider(TestFeatureFlags flags) =>
        new(flags, _world.LinkService(), _world.SiteLinks, NullLogger<GuestStayLinkProvider>.Instance);

    /// <summary>The check-in link of the stay, issued the way the daily job issues it (one session, the lifetime of the job).</summary>
    private async Task<IssuedCheckInLink> IssueCheckInLinkAsync(bool reminder = false)
    {
        var checkIn = new GuestCheckInService(
            _world.Db,
            NullLogger<GuestCheckInService>.Instance,
            options: Microsoft.Extensions.Options.Options.Create(new GuestCheckInOptions { SessionLifetimeDays = 7 }),
            timeProvider: _world.Clock);
        var issued = await checkIn.IssueLinkAsync(_world.Booking.Id, _world.Org.Id);
        if (reminder)
        {
            var session = await _world.Db.GuestCheckInSessions.SingleAsync(s => s.Id == issued.SessionId);
            session.ReminderSentAt = GuestPortalTestWorld.Now.UtcDateTime;
            await _world.Db.SaveChangesAsync();
        }

        return issued;
    }

    /// <summary>The stay of the test world is an Airbnb one: the confirmation of a direct booking needs a direct one.</summary>
    private async Task MakeDirectBookingAsync(bool noShow = false)
    {
        var booking = await _world.Db.Bookings.SingleAsync(b => b.Id == _world.Booking.Id);
        booking.Source = BookingSource.Direct;
        if (noShow)
            booking.NoShowAt = GuestPortalTestWorld.Now.UtcDateTime;
        await _world.Db.SaveChangesAsync();
        _world.Db.ChangeTracker.Clear();
    }

    private EmailContent ConfirmationOf(RecordingEmailQueue queue) =>
        Assert.Single(queue.Snapshot(), e => e.To == GuestEmail && e.Template == EmailTemplates.Names.GuestBookingConfirmed).Content;

    /// <summary>The e-mail service of the job: keeps what it was asked to send, whichever of the two ways it is asked.</summary>
    private sealed class CapturingEmailService(EmailSendResult? result = null) : IEmailService
    {
        public List<(string To, string Subject, string Html, string? ReplyTo, string? FromName)> Sent { get; } = [];

        public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
        {
            Sent.Add((to, subject, htmlContent, null, null));
            return Task.FromResult(result ?? EmailSendResult.Sent());
        }

        public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? replyTo, string? fromName)
        {
            Sent.Add((to, subject, htmlContent, replyTo, fromName));
            return Task.FromResult(result ?? EmailSendResult.Sent());
        }
    }
}
