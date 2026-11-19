using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E23/F02/US01/T01 (NF-122, NF-63): the e-mail that confirms a direct booking carries the check-in link when the online check-in is already open for the stay and says on which day
/// the link arrives when it is not, on the real services over a database. The checkout tells the guest that the link arrives at their address; before this the confirmation had no link
/// and said nothing of when it would arrive, and a last-minute booking got its link only at the next daily run, which can come after the arrival. The proofs the brief asks for: a stay
/// confirmed inside the window gets a working link in the confirmation and the next run of the job sends nothing more; a stay confirmed beyond it gets the day (the job sends the link on
/// that day); and the rules the e-mail must keep: nothing about a link that is not needed, a confirmation that still goes out when the link cannot be issued, and no personal data nor token
/// in a log.
/// </summary>
public sealed class BookingConfirmedCheckInLinkTests : IDisposable
{
    private const string GuestEmail = "anna.verdi@example.com";

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private static readonly Regex CheckInLink = new("https://casazen-app\\.test/checkin/([0-9a-f]{64})", RegexOptions.Compiled);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"booking-confirmed-checkin-{Guid.NewGuid():N}")
        .Options);

    private readonly FixedTimeProvider _clock = new(Now);
    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();
    private readonly IOptions<GuestCheckInOptions> _options = Microsoft.Extensions.Options.Options.Create(new GuestCheckInOptions { SendWindowDays = 3, SessionLifetimeDays = 7 });
    private readonly CapturingLogger<BookingNotifier> _notifierLog = new();
    private readonly CapturingLogger<CheckInLinkDeliveryService> _deliveryLog = new();

    /// <summary>The job client of <see cref="BookingConfirmationEmailJob"/>: what it schedules for a temporary failure is what the tests read.</summary>
    private readonly Mock<Hangfire.IBackgroundJobClient> _scheduler = new();
    private readonly GuestCheckInService _checkIn;
    private readonly CheckInLinkDeliveryService _deliveries;

    public BookingConfirmedCheckInLinkTests()
    {
        _checkIn = new GuestCheckInService(_db, NullLogger<GuestCheckInService>.Instance, options: _options, timeProvider: _clock);
        _deliveries = new CheckInLinkDeliveryService(
            _db,
            Mock.Of<IGuestCheckInLinkEmailQueue>(),
            Mock.Of<IMessageReplyService>(),
            Mock.Of<IGuestContactInfoService>(),
            Mock.Of<IOctorateBookingTargets>(),
            Mock.Of<IInternalActivityService>(),
            new TestFeatureFlags(),
            EmailTestHelpers.Links(),
            _deliveryLog,
            _clock);
    }

    public void Dispose() => _db.Dispose();

    // ---- The proofs of the brief -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (AC1). A direct booking confirmed with the arrival inside the window gets ONE confirmation that carries a working check-in link, issued the way the job
    /// issues it (one session, the lifetime of the job). The link is «in attesa» (the session says queued, the delivery record Email / Sending) until the provider accepts the e-mail, and
    /// then it is sent, on the session and in the delivery record; nothing is queued for the guest but that e-mail, and the next run of the daily job finds a usable link and sends nothing
    /// more. The link is in no log and in no row.
    /// </summary>
    [Fact]
    public async Task ConfirmedInsideWindow_LinkInConfirmation_NoDuplicateFromJob_Test()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 2);

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var confirmation = Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail);
        Assert.Equal(EmailTemplates.Names.GuestBookingConfirmed, confirmation.Template);
        var html = confirmation.Content.HtmlBody;
        var token = CheckInLink.Match(html).Groups[1].Value;
        Assert.Equal(64, token.Length);

        // The button «Fai il check-in online», the usual fallback link under it (the raw link, readable and clickable), and until when the link is valid.
        var link = $"https://casazen-app.test/checkin/{token}";
        Assert.Contains($"<a href=\"{link}\" style=", html, StringComparison.Ordinal);
        Assert.Contains("Fai il check-in online</a>", html, StringComparison.Ordinal);
        Assert.Contains($"copia questo link nel browser:<br /><a href=\"{link}\">{link}</a>", html, StringComparison.Ordinal);
        Assert.Contains("Il link è valido fino al 16/10/2026 11:00.", html, StringComparison.Ordinal);

        // A working link: the one session of the booking, open, with the lifetime of the job, and the token of the e-mail opens it.
        var session = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInService.HashToken(token), session.TokenHash);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
        Assert.Equal(Now.UtcDateTime.AddDays(7), session.ExpiresAt);
        Assert.NotNull(await _checkIn.GetSessionByTokenAsync(token));

        // «In attesa» while the e-mail is with the queue: the session says queued, with no time of sending, and the delivery record is Email / Sending with no token in it. The e-mail
        // knows the session of its link, so that the job that sends it can write the outcome there.
        Assert.Equal(new CheckInLinkEmail(bookingId, session.Id), confirmation.Content.CheckInLink);
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, session.LinkEmailStatus);
        Assert.Null(session.SentAt);
        var waiting = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.Sending), (waiting.Channel, waiting.State));
        Assert.Equal(session.Id, waiting.SessionId);
        Assert.Null(waiting.LinkToken);

        // Sent when the provider accepts it: the session says so as the e-mail of the link would, and the delivery record is Email / Sent.
        await RunConfirmationJobAsync(EmailSendResult.Sent());
        var sent = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Sent, sent.LinkEmailStatus);
        Assert.Equal(Now.UtcDateTime, sent.SentAt);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.Sent), (row.Channel, row.State));
        Assert.Equal(session.Id, row.SessionId);
        Assert.Null(row.LinkToken);

        // The next run of the daily job: the link is a usable one, so it queues no e-mail and issues no second link.
        var linkEmails = QueuedLinkEmails();
        await Job(linkEmails, _clock).ExecuteAsync();

        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(await _db.GuestCheckInSessions.AsNoTracking().ToListAsync());
        Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail);

        // Privacy: no address, name or token in what the services logged.
        AssertNoPersonalDataInTheLogs(token);
    }

    /// <summary>
    /// PROOF TEST: data rule (AC2). A direct booking confirmed with the arrival beyond the window gets a confirmation with NO link that says the day it arrives (the day the check-in
    /// opens, formatted like the other dates of the e-mail) and that it can be requested from «Le mie prenotazioni» from then; no link is issued and no record is written. The job sends the
    /// link on that day, as it does today, and not the day before.
    /// </summary>
    [Fact]
    public async Task ConfirmedBeyondWindow_NoLink_DayStated_Test()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 11);

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var html = Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail).Content.HtmlBody;
        Assert.Contains("Il link per il check-in online ti arriverà via email il <strong>17/10/2026</strong>.", html, StringComparison.Ordinal);
        Assert.Contains("Da quel giorno puoi anche richiederlo in «Le mie prenotazioni».", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/checkin/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Fai il check-in online", html, StringComparison.Ordinal);
        Assert.Empty(await _db.GuestCheckInSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.CheckInLinkDeliveries.AsNoTracking().ToListAsync());

        // The day before it opens the job sends nothing; on the day it issues the link and queues its e-mail.
        var linkEmails = QueuedLinkEmails();
        await Job(linkEmails, AtEightUtc(2026, 10, 16)).ExecuteAsync();
        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await Job(linkEmails, AtEightUtc(2026, 10, 17)).ExecuteAsync();
        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var session = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
    }

    // ---- The rules the e-mail keeps ----------------------------------------------------------------------------------------------------

    /// <summary>The same Europe/Rome calendar window as the job, whatever the hour: the last day of the window gets the link, the day after it gets the day.</summary>
    [Theory]
    [InlineData("2026-10-09T12:00:00Z")]
    [InlineData("2026-10-09T23:30:00Z")]
    public async Task ConfirmedOnTheLastDayOfTheWindow_GetsTheLink_AtAnyHourUtc(string utcNow)
    {
        // At 23:30 UTC Rome is already on the next day: an instant bound (now + 3 days) would drop the last day of the window.
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));
        var last = await SeedAsync(daysUntilArrival: 3, clock: clock);
        var beyond = await SeedAsync(daysUntilArrival: 4, clock: clock);
        var notifier = new BookingNotifier(
            _db, _emails, EmailTestHelpers.Links(), _pushes, NullLogger<BookingNotifier>.Instance, _checkIn, _deliveries, _options, clock);

        await notifier.BookingConfirmedAsync(last, BookingConfirmationKind.PaidOnline);
        await notifier.BookingConfirmedAsync(beyond, BookingConfirmationKind.PaidOnline);

        Assert.True(await _db.GuestCheckInSessions.AnyAsync(s => s.BookingId == last));
        Assert.False(await _db.GuestCheckInSessions.AnyAsync(s => s.BookingId == beyond));
    }

    /// <summary>A check-in that is not needed or already done (the cases where the guest page says «completed» or nothing) is not mentioned: no link, no day, no issued session.</summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, 1, true)]
    [InlineData(BookingStatus.CheckedOut, -3, false)]
    public async Task CheckInNotNeededOrDone_SaysNothingAboutTheLink(BookingStatus status, int daysUntilArrival, bool communicationSent)
    {
        var bookingId = await SeedAsync(daysUntilArrival, status);
        if (communicationSent)
        {
            _db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
            {
                BookingId = bookingId,
                GuestId = (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).GuestId,
                Status = AlloggiatiWebStatus.InviatoManualmente,
                ReportedAt = _clock.TodayInRome(),
            });
            await _db.SaveChangesAsync();
        }

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var html = Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail).Content.HtmlBody;
        Assert.DoesNotContain("check-in", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/checkin/", html, StringComparison.Ordinal);
        Assert.Empty(await _db.GuestCheckInSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.CheckInLinkDeliveries.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The link cannot be issued: the confirmation still goes out, with no link and the day the job's next run sends it (the next day on the Rome calendar), and the job retries as it
    /// does today. The log says the booking and nothing of the guest.
    /// </summary>
    [Fact]
    public async Task LinkCannotBeIssued_ConfirmationStillSent_WithTheDay_AndTheJobRetries()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 2);
        var broken = new Mock<IGuestCheckInService>();
        broken.Setup(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>())).ThrowsAsync(new InvalidOperationException("the database is not there"));

        await Notifier(broken.Object).BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var html = Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail).Content.HtmlBody;
        Assert.DoesNotContain("/checkin/", html, StringComparison.Ordinal);
        Assert.Contains("Il link per il check-in online ti arriverà via email il <strong>10/10/2026</strong>.", html, StringComparison.Ordinal);
        Assert.Empty(await _db.CheckInLinkDeliveries.AsNoTracking().ToListAsync());
        Assert.Contains(_notifierLog.Messages, m => m.Contains("could not be issued", StringComparison.Ordinal) && m.Contains(bookingId.ToString("D"), StringComparison.Ordinal));
        AssertNoPersonalDataInTheLogs(token: null);

        var linkEmails = QueuedLinkEmails();
        await Job(linkEmails, _clock).ExecuteAsync();
        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A confirmation that cannot be queued (no e-mail provider, a queue that refuses it) leaves no link behind: the session just issued would stop the job from sending one, so it is
    /// taken back (it expires, it is failed with its code, nothing is recorded as sent) and the job issues and sends a link at its next run.
    /// </summary>
    [Fact]
    public async Task ConfirmationNotQueued_TheLinkIsTakenBack_AndTheJobSendsIt()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 2);
        _emails.Refuse = true;

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        Assert.Empty(_emails.Snapshot());
        var taken = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, taken.Status);
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, taken.LinkEmailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.QueueFailed, taken.LinkEmailError);
        Assert.Null(taken.SentAt);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, GuestCheckInLinkEmailErrors.QueueFailed), (row.Channel, row.State, row.LastErrorCode));

        var linkEmails = QueuedLinkEmails();
        await Job(linkEmails, _clock).ExecuteAsync();
        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var sessions = await _db.GuestCheckInSessions.AsNoTracking().Where(s => s.BookingId == bookingId).ToListAsync();
        Assert.Contains(sessions, s => s.Status == GuestCheckInSessionStatus.Scaduto);
        Assert.Contains(sessions, s => s.Status == GuestCheckInSessionStatus.Inviato);
    }

    /// <summary>
    /// PROOF TEST: data rule (CO-09, A5-26: never «sent» when nothing left). The provider refuses the e-mail that carries the link (a refusal that is final): the link is NOT sent, the
    /// session and the delivery record say failed with the code, and the link, which nobody has, is taken back, so that the next run of the daily job issues and e-mails a new one, once.
    /// The confirmation itself is not sent again, and nothing is retried.
    /// </summary>
    [Fact]
    public async Task ConfirmationRefusedByTheProvider_LinkFailedAndTakenBack_AndTheJobSendsOneNewLink()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 2);
        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        await RunConfirmationJobAsync(new EmailSendResult(false, "InvalidRecipient", IsTransient: false));

        var failed = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, failed.LinkEmailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.Rejected, failed.LinkEmailError);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, failed.Status);
        Assert.Null(failed.SentAt);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.Failed, GuestCheckInLinkEmailErrors.Rejected), (row.Channel, row.State, row.LastErrorCode));
        _scheduler.Verify(c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);

        // No usable link is left, so the daily job issues one and e-mails it, once: never a second link while the first may still reach the guest.
        var linkEmails = QueuedLinkEmails();
        await Job(linkEmails, _clock).ExecuteAsync();
        linkEmails.Verify(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var sessions = await _db.GuestCheckInSessions.AsNoTracking().Where(s => s.BookingId == bookingId).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.Status == GuestCheckInSessionStatus.Inviato);
        Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail);
    }

    /// <summary>
    /// A temporary provider error is tried again by the job of the confirmation (the next attempt is scheduled and the link stays «in attesa», not sent and not failed), and the last
    /// attempt that fails gives failed with its own code, not delivered: the link is taken back as for a refusal.
    /// </summary>
    [Fact]
    public async Task ConfirmationTemporarilyNotDelivered_IsTriedAgain_AndFailsAfterTheLastAttempt()
    {
        var bookingId = await SeedAsync(daysUntilArrival: 2);
        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);
        var transient = new EmailSendResult(false, "RateLimitExceeded", IsTransient: true);

        await RunConfirmationJobAsync(transient, attempt: 1);

        _scheduler.Verify(
            c => c.Create(
                It.Is<Hangfire.Common.Job>(job => job.Type == typeof(BookingConfirmationEmailJob) && (int)job.Args[6] == 2),
                It.IsAny<Hangfire.States.IState>()),
            Times.Once);
        var waiting = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, waiting.LinkEmailStatus);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, waiting.Status);

        await RunConfirmationJobAsync(transient, attempt: BookingConfirmationEmailJob.MaxAttempts);

        var failed = await _db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, failed.LinkEmailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.NotDelivered, failed.LinkEmailError);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, failed.Status);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((CheckInLinkDeliveryState.Failed, GuestCheckInLinkEmailErrors.NotDelivered), (row.State, row.LastErrorCode));
        _scheduler.Verify(c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Once);
    }

    // ---- The data every test runs on ---------------------------------------------------------------------------------------------------

    private BookingNotifier Notifier(IGuestCheckInService? checkIn = null) =>
        new(_db, _emails, EmailTestHelpers.Links(), _pushes, _notifierLog, checkIn ?? _checkIn, _deliveries, _options, _clock);

    /// <summary>
    /// What the Hangfire worker does with the confirmation that was queued for the guest: the job of the confirmation runs it with the arguments the queue gave it, the provider
    /// answering <paramref name="result"/>.
    /// </summary>
    private async Task RunConfirmationJobAsync(EmailSendResult result, int attempt = 1)
    {
        var (to, content, template) = Assert.Single(_emails.Snapshot(), e => e.To == GuestEmail);
        var link = Assert.IsType<CheckInLinkEmail>(content.CheckInLink);
        var emailService = new Mock<IEmailService>();
        emailService.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(result);
        var job = new BookingConfirmationEmailJob(emailService.Object, _deliveries, _scheduler.Object, NullLogger<BookingConfirmationEmailJob>.Instance);

        await job.SendAsync(to!, content.Subject, content.HtmlBody, template, link.BookingId, link.SessionId, attempt);
    }

    /// <summary>The real daily job over the same database, with the queue of the link e-mails watched: its calls are what a duplicate would be.</summary>
    private GuestCheckInSendJob Job(Mock<IGuestCheckInLinkEmailQueue> linkEmails, TimeProvider clock) =>
        new(
            _db,
            new GuestCheckInService(_db, NullLogger<GuestCheckInService>.Instance, options: _options, timeProvider: clock),
            linkEmails.Object,
            new StayGuestService(_db, new AlloggiatiCodeTableService(_db, NullLogger<AlloggiatiCodeTableService>.Instance)),
            EmailTestHelpers.Links(),
            _options,
            NullLogger<GuestCheckInSendJob>.Instance,
            clock,
            _deliveries);

    private static Mock<IGuestCheckInLinkEmailQueue> QueuedLinkEmails()
    {
        var linkEmails = new Mock<IGuestCheckInLinkEmailQueue>();
        linkEmails
            .Setup(q => q.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Queued));
        return linkEmails;
    }

    /// <summary>08:00 UTC of a day: the time of the daily run.</summary>
    private static FixedTimeProvider AtEightUtc(int year, int month, int day) => new(new DateTimeOffset(year, month, day, 8, 0, 0, TimeSpan.Zero));

    private void AssertNoPersonalDataInTheLogs(string? token)
    {
        foreach (var message in _notifierLog.Messages.Concat(_deliveryLog.Messages))
        {
            Assert.DoesNotContain(GuestEmail, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Anna", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Verdi", message, StringComparison.Ordinal);
            if (token is not null)
                Assert.DoesNotContain(token, message, StringComparison.Ordinal);
        }
    }

    private async Task<Guid> SeedAsync(int daysUntilArrival, BookingStatus status = BookingStatus.Confirmed, TimeProvider? clock = null)
    {
        var today = (clock ?? _clock).TodayInRome();
        var org = new OrgEntity { Name = "Villa Org", Slug = "villa-org", DisplayName = "Villa Org", ContactEmail = "host@example.com" };
        var property = new Property { OrgId = org.Id, OwnerId = "auth0|host", Name = "Villa Rosa", Address = "Via Roma 1", City = "Roma" };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = GuestEmail };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(daysUntilArrival),
            CheckOutDate = today.AddDays(daysUntilArrival + 3),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TotalPrice = 362m,
        };
        _db.AddRange(org, property, guest, booking);
        _db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = 362m,
            Status = PaymentStatus.Completed,
            TransactionId = $"pi_{Guid.NewGuid():N}",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking.Id;
    }

    /// <summary>Keeps what a service logged, as the text a person would read in the log (the exception is not part of it).</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, null));
        }
    }
}
