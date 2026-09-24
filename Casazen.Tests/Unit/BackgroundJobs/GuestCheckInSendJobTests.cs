using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// Daily check-in link job and the link email job (US-020, CO-09): a failed email keeps the link usable and tells the host
/// (A5-26), an expired link is replaced (A5-27), complete guest data needs no link.
/// </summary>
public class GuestCheckInSendJobTests
{
    [Fact]
    public async Task ExecuteAsync_EmailRejected_KeepsLinkOpenWithFailedEmailAndIssuesNoSecondLink()
    {
        await using var db = CreateContext();
        var booking = await SeedBookingEntityAsync(db, BookingStatus.Confirmed, daysUntilCheckIn: 1);
        var harness = new Harness(db, EmailResult(new EmailSendResult(false, "invalid address")));

        await harness.RunSendJobAsync();
        await harness.RunQueuedEmailJobsAsync();
        await harness.RunSendJobAsync();

        var session = Assert.Single(await db.GuestCheckInSessions.Where(s => s.BookingId == booking.Id).ToListAsync());
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, session.LinkEmailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.Rejected, session.LinkEmailError);
        Assert.Null(session.SentAt);
        harness.Email.Verify(s => s.SendEmailAsync(booking.Guest.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CheckedInBookingWithoutSession_SendsGuestLinkAndRecordsSent()
    {
        await using var db = CreateContext();
        var (bookingId, _) = await SeedBookingAsync(db, BookingStatus.CheckedIn);
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));

        await harness.RunSendJobAsync();
        var session = await db.GuestCheckInSessions.SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, session.LinkEmailStatus);
        await harness.RunQueuedEmailJobsAsync();

        var (to, subject, html) = Assert.Single(harness.SentEmails);
        Assert.Equal("anna@example.com", to);
        Assert.Contains("Completa il check-in", subject);
        Assert.Matches("https://public\\.example/checkin/[0-9a-f]{64}", html);
        Assert.Contains("Il link è valido fino al", html);
        await db.Entry(session).ReloadAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Sent, session.LinkEmailStatus);
        Assert.NotNull(session.SentAt);
        Assert.Equal(TimeSpan.FromDays(7), session.ExpiresAt - session.CreatedAt);
    }

    [Fact]
    public async Task ExecuteAsync_SessionLifetimeConfigured_LinkExpiresAfterConfiguredDays()
    {
        await using var db = CreateContext();
        var (bookingId, _) = await SeedBookingAsync(db, BookingStatus.Confirmed);
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()), sessionLifetimeDays: 2);

        await harness.RunSendJobAsync();

        var session = await db.GuestCheckInSessions.SingleAsync(s => s.BookingId == bookingId);
        Assert.Equal(TimeSpan.FromDays(2), session.ExpiresAt - session.CreatedAt);
    }

    [Fact]
    public async Task ExecuteAsync_LinkExpiredByTime_MarksItExpiredAndIssuesANewLink()
    {
        await using var db = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(db, BookingStatus.CheckedIn);
        var stale = new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = orgId,
            TokenHash = GuestCheckInService.HashToken("old-token"),
            Status = GuestCheckInSessionStatus.InCompilazione,
            CreatedAt = DateTime.UtcNow.AddDays(-8),
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        };
        db.GuestCheckInSessions.Add(stale);
        await db.SaveChangesAsync();
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));

        await harness.RunSendJobAsync();

        var sessions = await db.GuestCheckInSessions.Where(s => s.BookingId == bookingId).OrderBy(s => s.CreatedAt).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, sessions[0].Status);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, sessions[1].Status);
        Assert.True(sessions[1].ExpiresAt > DateTime.UtcNow);
        Assert.Single(harness.QueuedJobs);
    }

    [Fact]
    public async Task ExecuteAsync_CheckedInBookingWithReportDeclaredSent_DoesNotIssueLink()
    {
        await using var db = CreateContext();
        var (bookingId, _) = await SeedBookingAsync(db, BookingStatus.CheckedIn);
        db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = bookingId,
            GuestId = db.Bookings.Single(b => b.Id == bookingId).GuestId,
            Status = AlloggiatiWebStatus.InviatoManualmente,
            ReportedAt = DateTime.UtcNow.Date,
        });
        await db.SaveChangesAsync();
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));

        await harness.RunSendJobAsync();

        Assert.Empty(await db.GuestCheckInSessions.ToListAsync());
        Assert.Empty(harness.QueuedJobs);
    }

    [Fact]
    public async Task ExecuteAsync_GuestDataCompletedByHost_DoesNotIssueLink()
    {
        await using var db = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(db, BookingStatus.Confirmed);
        db.StayGuests.Add(CompleteSingleGuest(bookingId, orgId));
        await db.SaveChangesAsync();
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));

        await harness.RunSendJobAsync();

        Assert.Empty(await db.GuestCheckInSessions.ToListAsync());
        Assert.Empty(harness.QueuedJobs);
    }

    [Fact]
    public async Task ExecuteAsync_PublicSiteBaseUrlMissing_CreatesNoSessionAndQueuesNothing()
    {
        await using var db = CreateContext();
        await SeedBookingAsync(db, BookingStatus.Confirmed);
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()), publicSiteBaseUrl: null);

        await harness.RunSendJobAsync();

        Assert.Empty(await db.GuestCheckInSessions.ToListAsync());
        Assert.Empty(harness.QueuedJobs);
    }

    [Fact]
    public async Task ExecuteAsync_GuestNameWithMarkup_IsHtmlEncodedInEmail()
    {
        await using var db = CreateContext();
        var (bookingId, _) = await SeedBookingAsync(db, BookingStatus.CheckedIn);
        var guest = db.Bookings.Include(b => b.Guest).Single(b => b.Id == bookingId).Guest;
        guest.FirstName = "<a href=\"https://phish.example\">Paga qui</a>";
        await db.SaveChangesAsync();
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));

        await harness.RunSendJobAsync();
        await harness.RunQueuedEmailJobsAsync();

        var (_, _, html) = Assert.Single(harness.SentEmails);
        Assert.DoesNotContain("<a href=\"https://phish.example\"", html);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;", html);
    }

    [Fact]
    public async Task SendAsync_TransientFailure_SchedulesRetryThenFailsAfterLastAttempt()
    {
        await using var db = CreateContext();
        await SeedBookingAsync(db, BookingStatus.Confirmed);
        var harness = new Harness(db, EmailResult(new EmailSendResult(false, "503", IsTransient: true)));
        await harness.RunSendJobAsync();
        var (sessionId, token, _) = Assert.Single(harness.QueuedJobs);

        await harness.EmailJob().SendAsync(sessionId, token, 1);

        var retry = Assert.Single(harness.ScheduledJobs);
        Assert.Equal(2, retry.Attempt);
        var session = await db.GuestCheckInSessions.SingleAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, session.LinkEmailStatus);

        await harness.EmailJob().SendAsync(sessionId, token, GuestCheckInLinkEmailJob.MaxAttempts);

        await db.Entry(session).ReloadAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, session.LinkEmailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.NotDelivered, session.LinkEmailError);
        Assert.Single(harness.ScheduledJobs);
    }

    [Fact]
    public async Task SendAsync_LinkReplacedBeforeTheEmailLeft_SendsNothing()
    {
        await using var db = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(db, BookingStatus.Confirmed);
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));
        await harness.RunSendJobAsync();
        var (sessionId, token, _) = Assert.Single(harness.QueuedJobs);
        await harness.CheckIn.IssueLinkAsync(bookingId, orgId);

        await harness.EmailJob().SendAsync(sessionId, token, 1);

        Assert.Empty(harness.SentEmails);
        var replaced = await db.GuestCheckInSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, replaced.Status);
        Assert.Equal(GuestCheckInLinkEmailErrors.LinkNotUsable, replaced.LinkEmailError);
    }

    [Fact]
    public async Task QueueAsync_GuestWithoutEmail_RecordsFailedWithoutQueueing()
    {
        await using var db = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(db, BookingStatus.Confirmed);
        db.Bookings.Include(b => b.Guest).Single(b => b.Id == bookingId).Guest.Email = " ";
        await db.SaveChangesAsync();
        var harness = new Harness(db, EmailResult(EmailSendResult.Sent()));
        var link = await harness.CheckIn.IssueLinkAsync(bookingId, orgId);

        var outcome = await harness.Queue().QueueAsync(link.SessionId, link.Token);

        Assert.Equal(new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.NoRecipient), outcome);
        Assert.Empty(harness.QueuedJobs);
        var session = await db.GuestCheckInSessions.SingleAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Failed, session.LinkEmailStatus);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, session.Status);
    }

    private static EmailSendResult EmailResult(EmailSendResult result) => result;

    private static StayGuest CompleteSingleGuest(Guid bookingId, Guid orgId) => new()
    {
        BookingId = bookingId,
        OrgId = orgId,
        Position = 0,
        Type = StayGuestType.SingleGuest,
        FirstName = "Anna",
        LastName = "Bianchi",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1990, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Roma",
        BirthProvince = "RM",
        CitizenshipName = "Italia",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CA12345AB",
        DocumentIssuePlaceName = "Roma",
        DataSource = StayGuestDataSource.Host,
        EnteredByUserId = "auth0|owner",
    };

    /// <summary>Real services on the in-memory database; Hangfire and the email provider are recorded.</summary>
    private sealed class Harness
    {
        private readonly AppDbContext _db;
        private readonly Mock<IBackgroundJobClient> _jobs = new();
        private readonly string? _publicSiteBaseUrl;
        private readonly IOptions<GuestCheckInOptions> _options;

        public Harness(AppDbContext db, EmailSendResult emailResult, string? publicSiteBaseUrl = "https://public.example", int sessionLifetimeDays = 7)
        {
            _db = db;
            _publicSiteBaseUrl = publicSiteBaseUrl;
            _options = Options.Create(new GuestCheckInOptions { SendWindowDays = 3, SessionLifetimeDays = sessionLifetimeDays });
            Email
                .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string>((to, subject, html) =>
                {
                    if (emailResult.Success)
                        SentEmails.Add((to, subject, html));
                })
                .ReturnsAsync(emailResult);
            _jobs
                .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                .Returns((Job job, IState state) =>
                {
                    var call = ((Guid)job.Args[0], (string)job.Args[1], (int)job.Args[2]);
                    if (state is ScheduledState)
                        ScheduledJobs.Add(new ScheduledCall(call.Item1, call.Item3));
                    else
                        QueuedJobs.Add(call);
                    return Guid.NewGuid().ToString();
                });
            CheckIn = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance, options: _options);
        }

        public Mock<IEmailService> Email { get; } = new();
        public GuestCheckInService CheckIn { get; }
        public List<(string To, string Subject, string Html)> SentEmails { get; } = [];
        public List<(Guid SessionId, string Token, int Attempt)> QueuedJobs { get; } = [];
        public List<ScheduledCall> ScheduledJobs { get; } = [];

        public GuestCheckInLinkEmailQueue Queue() =>
            new(_db, _jobs.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<GuestCheckInLinkEmailQueue>.Instance);

        public GuestCheckInLinkEmailJob EmailJob() =>
            new(_db, Email.Object, EmailTestHelpers.Links(_publicSiteBaseUrl), _jobs.Object, NullLogger<GuestCheckInLinkEmailJob>.Instance);

        public Task RunSendJobAsync() =>
            new GuestCheckInSendJob(
                _db,
                CheckIn,
                Queue(),
                new StayGuestService(_db, new AlloggiatiCodeTableService(_db, NullLogger<AlloggiatiCodeTableService>.Instance)),
                EmailTestHelpers.Links(_publicSiteBaseUrl),
                _options,
                Mock.Of<ILogger<GuestCheckInSendJob>>()).ExecuteAsync();

        public async Task RunQueuedEmailJobsAsync()
        {
            foreach (var (sessionId, token, attempt) in QueuedJobs.ToList())
                await EmailJob().SendAsync(sessionId, token, attempt);
        }
    }

    private sealed record ScheduledCall(Guid SessionId, int Attempt);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Guid BookingId, Guid OrgId)> SeedBookingAsync(
        AppDbContext context,
        BookingStatus status)
    {
        var booking = await SeedBookingEntityAsync(context, status, daysUntilCheckIn: 0);
        return (booking.Id, booking.OrgId);
    }

    private static async Task<Booking> SeedBookingEntityAsync(
        AppDbContext context,
        BookingStatus status,
        int daysUntilCheckIn)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        var org = new OrgEntity
        {
            Id = orgId,
            Name = "Test Org",
            Slug = $"org-{orgId:N}",
            DisplayName = "Test Org",
            ContactEmail = "host@example.com",
        };

        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
        };

        var guest = new Guest
        {
            Id = guestId,
            OrgId = orgId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
        };

        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            OrgId = orgId,
            GuestId = guestId,
            Guest = guest,
            Property = property,
            Org = org,
            CheckInDate = DateTime.UtcNow.Date.AddDays(daysUntilCheckIn),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(daysUntilCheckIn + 2),
            Status = status,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 100m,
        };

        context.Orgs.Add(org);
        context.Properties.Add(property);
        context.Guests.Add(guest);
        context.Bookings.Add(booking);

        await context.SaveChangesAsync();
        return booking;
    }
}
