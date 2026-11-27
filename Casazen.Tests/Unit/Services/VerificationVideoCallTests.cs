using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E22/F01/US01/T03: the operator video call, the fallback when the face check cannot decide. The operator creates the meeting where it likes and gives CasaZen
/// its address; CasaZen e-mails it to the guest, in the guest's language, and keeps only that a call was proposed, and when (never the link, in no table or log of its own). A guest
/// with no e-mail address is «unreachable» and the "To do" suggests the portal chat. The decision after the call is the one the queue already has.
/// </summary>
public class VerificationVideoCallTests : IAsyncLifetime
{
    private const string Marker = "ROOM-7f3a9c41";
    private const string MeetingUrl = "https://meet.example.test/" + Marker;
    private const string Operator = "auth0|operator-1";
    private const string OwnerId = "auth0|owner";
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(Now);
    private readonly List<string> _logs = [];
    private readonly List<(string? To, EmailContent Content, string Template)> _emails = [];
    private readonly Mock<IEmailQueue> _queue = new();
    private AppDbContext _db = null!;
    private Guid _orgId;
    private Guid _propertyId;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(_options);
        _queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => _emails.Add((to, content, template)))
            .Returns(true);

        var org = HostScopeScenario.NewOrg("Agenzia video", $"video-{Guid.NewGuid():N}"[..20]);
        var property = HostScopeScenario.NewProperty(org.Id, OwnerId, "Casa Video");
        _db.Orgs.Add(org);
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        _orgId = org.Id;
        _propertyId = property.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private HostScope OrgScope => new(_orgId);

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (privacy): CasaZen keeps no trace of the link. The e-mail carries it, once, to the guest; no row of the model holds it (the session, the log of the
    /// verifications, the "To do", the activity log: every column of every table is read) and no line of the application log does; what is kept is only that
    /// a call was proposed, and when, on the "To do" that waits for the operator, which says so in its reason. <b>What it does not prove</b>: the e-mail queue is
    /// a fake here, so the body of the queued message (the Hangfire job, which holds the link until the job expires, 24 hours by default after a successful
    /// send) is outside it; the PR says so under its risks and its manual follow-up.
    /// </summary>
    [Fact]
    public async Task VideoCall_LinkNotPersisted_Test()
    {
        var bookingId = await AddStayAsync("anna@example.com");

        var outcome = await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);

        Assert.Equal(VideoCallOutcome.Sent, outcome);
        var mail = Assert.Single(_emails);
        Assert.Equal("anna@example.com", mail.To);
        Assert.Equal(EmailTemplates.Names.GuestVideoCall, mail.Template);
        Assert.Equal("Videochiamata per il check-in — Casa Video", mail.Content.Subject);
        Assert.Contains($"href=\"{MeetingUrl}\"", mail.Content.HtmlBody, StringComparison.Ordinal);

        // Nowhere in the database, nowhere in the logs.
        AssertNoRowHolds(Marker);
        AssertNoRowHolds("meet.example.test");
        Assert.All(_logs, line => Assert.DoesNotContain(Marker, line, StringComparison.Ordinal));

        // What is kept: that a call was proposed, and when, in words (the id of the operator is in the log, not in the "To do"), and the state the operator sees.
        var todo = await _db.InternalActivities.SingleAsync(Ct);
        Assert.Equal(EmailTexts.Get("TodoReason_IdentityVerificationVideoCall", EmailTemplates.DefaultCulture), todo.Reason);
        Assert.Equal("Videochiamata proposta il 2026-11-14 10:00 (UTC)", todo.Notes);
        Assert.DoesNotContain(Operator, todo.Notes, StringComparison.Ordinal);
        Assert.Null(todo.CompletedAt);
        Assert.Contains(_logs, line => line.Contains(Operator, StringComparison.Ordinal) && line.Contains("video call", StringComparison.Ordinal));

        // The verification still waits for the operator: sending the link decided nothing.
        Assert.Single(await Service().GetPendingVerificationsAsync(Operator, OrgScope, Ct));
        Assert.False(await _db.BiometricVerificationLogs.AnyAsync(Ct));
    }

    /// <summary>The address of the meeting: an absolute https URL of at most 500 characters, no spaces, no credentials; anything else is refused (422) before anything is sent. It is never fetched.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://meet.example.test/room")]
    [InlineData("ftp://meet.example.test/room")]
    [InlineData("/room")]
    [InlineData("meet.example.test/room")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@meet.example.test/room")]
    [InlineData("https://meet.example.test/a b")]
    [InlineData("https://")]
    public async Task VideoCall_AnAddressThatIsNotHttpsAbsolute_IsRefusedAndNothingIsSent(string? url)
    {
        var bookingId = await AddStayAsync("anna@example.com");

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().ProposeVideoCallAsync(bookingId, OrgScope, url ?? string.Empty, Operator, Ct));

        Assert.Equal("video_call_url_invalid", refused.Code);
        Assert.Empty(_emails);
        Assert.Null((await _db.InternalActivities.SingleAsync(Ct)).Notes);
    }

    [Fact]
    public async Task VideoCall_TheLongestAddress_IsAccepted_OneCharacterMoreIsRefused_AndALineBreakIsNotAnAddress()
    {
        var bookingId = await AddStayAsync("anna@example.com");
        var longest = "https://meet.example.test/" + new string('a', 500 - "https://meet.example.test/".Length);
        Assert.Equal(500, longest.Length);

        Assert.Equal(VideoCallOutcome.Sent, await Service().ProposeVideoCallAsync(bookingId, OrgScope, longest, Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().ProposeVideoCallAsync(bookingId, OrgScope, longest + "a", Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().ProposeVideoCallAsync(bookingId, OrgScope, "https://meet.example.test/a\nb", Operator, Ct));
        Assert.Single(_emails);

        // The case of the scheme and the host does not matter; a bare host is an address.
        Assert.Equal(VideoCallOutcome.Sent, await Service().ProposeVideoCallAsync(bookingId, OrgScope, "HTTPS://MEET.EXAMPLE.TEST", Operator, Ct));
        Assert.Contains("https://meet.example.test/", _emails[^1].Content.HtmlBody, StringComparison.Ordinal);
    }

    /// <summary>The address is checked by its shape only: the service has no way to reach it (no HTTP client, no handler, no factory among its dependencies).</summary>
    [Fact]
    public void VideoCall_TheAddressIsNeverFetched()
    {
        var parameters = typeof(VerificationQueueService).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(parameters, p => typeof(HttpClient).IsAssignableFrom(p) || typeof(HttpMessageHandler).IsAssignableFrom(p) || p == typeof(IHttpClientFactory));
        Assert.Equal("https://meet.example.test/room", VideoCallLinks.TryParse("  https://meet.example.test/room  ")!.AbsoluteUri);
        Assert.Null(VideoCallLinks.TryParse("http://meet.example.test/room"));
    }

    // ─── The guest who cannot be reached, the stays out of reach, the language ───────────────────────────

    /// <summary>A guest with no e-mail address: nothing is sent, the answer is «unreachable» and the "To do" suggests the portal chat; the link is in no row of the model.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VideoCall_AGuestWithNoEmail_IsUnreachable_AndTheTodoSuggestsThePortalChat(string? email)
    {
        var bookingId = await AddStayAsync(email);

        var outcome = await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);

        Assert.Equal(VideoCallOutcome.GuestUnreachable, outcome);
        Assert.Empty(_emails);
        var todo = await _db.InternalActivities.SingleAsync(Ct);
        Assert.Equal(EmailTexts.Get("TodoReason_IdentityVerificationUseChat", EmailTemplates.DefaultCulture), todo.Reason);
        Assert.Contains("chat", todo.Reason, StringComparison.OrdinalIgnoreCase);
        AssertNoRowHolds(Marker);
        Assert.Single(await Service().GetPendingVerificationsAsync(Operator, OrgScope, Ct));
    }

    /// <summary>
    /// Proof (permission, scope): only a stay the caller reaches, whose verification waits for an operator, gets a call. A collaborator whose scope is another
    /// property, an unknown booking, a verification already decided, one the agent accepted and a cancelled stay all answer «not pending» and send nothing.
    /// </summary>
    [Fact]
    public async Task VideoCall_OnlyAStayTheCallerReaches_WhoseVerificationWaits_GetsACall()
    {
        var bookingId = await AddStayAsync("anna@example.com");
        var foreign = new HostScope(_orgId, OwnerId: "auth0|somebody-else");

        Assert.Equal(VideoCallOutcome.NotPending, await Service().ProposeVideoCallAsync(bookingId, foreign, MeetingUrl, Operator, Ct));
        Assert.Equal(VideoCallOutcome.NotPending, await Service().ProposeVideoCallAsync(Guid.NewGuid(), OrgScope, MeetingUrl, Operator, Ct));

        var decided = await AddStayAsync("bruno@example.com");
        _db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = _orgId,
            BookingId = decided,
            GuestId = (await _db.Bookings.SingleAsync(b => b.Id == decided, Ct)).GuestId,
            Outcome = BiometricOutcome.Reject,
            Method = VerificationMethod.Operator,
            ModelName = "operator",
            ModelVersion = "n/a",
            ThresholdsJson = "{}",
            ReasonsJson = "[]",
            CreatedAt = Now.UtcDateTime,
        });
        await _db.SaveChangesAsync(Ct);
        Assert.Equal(VideoCallOutcome.NotPending, await Service().ProposeVideoCallAsync(decided, OrgScope, MeetingUrl, Operator, Ct));

        var accepted = await AddStayAsync("carla@example.com", outcome: BiometricOutcome.Accept);
        Assert.Equal(VideoCallOutcome.NotPending, await Service().ProposeVideoCallAsync(accepted, OrgScope, MeetingUrl, Operator, Ct));

        var cancelled = await AddStayAsync("dario@example.com", status: BookingStatus.Cancelled);
        Assert.Equal(VideoCallOutcome.NotPending, await Service().ProposeVideoCallAsync(cancelled, OrgScope, MeetingUrl, Operator, Ct));

        Assert.Empty(_emails);
        Assert.All(await _db.InternalActivities.ToListAsync(Ct), todo => Assert.Null(todo.Notes));
    }

    /// <summary>The e-mail is in the language the guest chose on the check-in page (Italian, English, German, French or Spanish), Italian when there is none.</summary>
    [Theory]
    [InlineData(null, "Videochiamata per il check-in")]
    [InlineData("it", "Videochiamata per il check-in")]
    [InlineData("en", "Video call for your check-in")]
    [InlineData("de", "Videoanruf für Ihren Check-in")]
    [InlineData("fr", "Appel vidéo pour votre enregistrement")]
    [InlineData("es", "Videollamada para tu check-in")]
    [InlineData("xx", "Videochiamata per il check-in")]
    public async Task VideoCall_TheEmailIsInTheLanguageOfTheGuest(string? language, string subjectStart)
    {
        var bookingId = await AddStayAsync("anna@example.com", language: language);

        await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);

        var mail = Assert.Single(_emails);
        Assert.StartsWith(subjectStart, mail.Content.Subject, StringComparison.Ordinal);
        Assert.Contains(Marker, mail.Content.HtmlBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line of the "To do" is a text of the host's side, in the language of the org (Italian or English) with the day and the hour of the proposal, and it never
    /// carries the id of the operator (a raw Auth0 id says nothing to the person who reads it): a second proposal adds a second line.
    /// </summary>
    [Theory]
    [InlineData("it", "Videochiamata proposta il 2026-11-14 10:00 (UTC)")]
    [InlineData("en", "Video call proposed on 2026-11-14 10:00 (UTC)")]
    public async Task VideoCall_TheTodoNote_IsInTheLanguageOfTheOrg_WithTheDate_AndWithoutTheOperator(string orgLanguage, string expected)
    {
        var org = await _db.Orgs.SingleAsync(Ct);
        org.DefaultLanguage = orgLanguage;
        await _db.SaveChangesAsync(Ct);
        var bookingId = await AddStayAsync("anna@example.com");

        await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, null, Ct);

        var todo = await _db.InternalActivities.SingleAsync(Ct);
        Assert.Equal(expected + "\n" + expected.Replace("10:00", "10:05", StringComparison.Ordinal), todo.Notes);
        Assert.DoesNotContain("auth0", todo.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EmailTexts.Get("TodoReason_IdentityVerificationVideoCall", CultureInfo.GetCultureInfo(orgLanguage)), todo.Reason);
    }

    /// <summary>When the e-mail cannot be queued (no provider, the queue down) nothing is recorded and the operator is told: the "To do" is not changed.</summary>
    [Fact]
    public async Task VideoCall_AnEmailThatCannotBeQueued_RecordsNothing()
    {
        var bookingId = await AddStayAsync("anna@example.com");
        _queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(false);

        var outcome = await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);

        Assert.Equal(VideoCallOutcome.NotSent, outcome);
        var todo = await _db.InternalActivities.SingleAsync(Ct);
        Assert.Null(todo.Notes);
        Assert.Equal(EmailTexts.Get("TodoReason_IdentityVerification", EmailTemplates.DefaultCulture), todo.Reason);
    }

    /// <summary>
    /// The decision after the call is the existing one: an approval of the queue with the note «videochiamata» is an operator decision (<c>Method = Operator</c>),
    /// and proposing a call before it did not close the verification or the "To do".
    /// </summary>
    [Fact]
    public async Task VideoCall_TheDecisionAfterTheCall_IsTheExistingApprovalOfTheOperator()
    {
        var bookingId = await AddStayAsync("anna@example.com");
        await Service().ProposeVideoCallAsync(bookingId, OrgScope, MeetingUrl, Operator, Ct);

        var decided = await Service().RecordManualVerificationAsync(bookingId, OrgScope, true, "videochiamata", Operator, Ct);

        Assert.True(decided);
        var row = await _db.BiometricVerificationLogs.SingleAsync(Ct);
        Assert.Equal(VerificationMethod.Operator, row.Method);
        Assert.Equal(BiometricOutcome.Accept, row.Outcome);
        Assert.Equal("videochiamata", row.OperatorNote);
        Assert.Equal(Operator, row.OperatorUserId);
        Assert.Empty(await Service().GetPendingVerificationsAsync(Operator, OrgScope, Ct));
    }

    // ─── The endpoint and the e-mail ─────────────────────────────────────────────────────────────────────

    /// <summary>The endpoint answers each outcome with its status and code, passes the caller and the address, and sits under the policy of the operators and behind the flag.</summary>
    [Theory]
    [InlineData(VideoCallOutcome.Sent, StatusCodes.Status200OK, null)]
    [InlineData(VideoCallOutcome.NotPending, StatusCodes.Status404NotFound, "no_pending_verification")]
    [InlineData(VideoCallOutcome.GuestUnreachable, StatusCodes.Status409Conflict, "guest_unreachable")]
    [InlineData(VideoCallOutcome.NotSent, StatusCodes.Status409Conflict, "video_call_not_sent")]
    public async Task Endpoint_AnswersEachOutcome(VideoCallOutcome outcome, int status, string? code)
    {
        var bookingId = Guid.NewGuid();
        var queue = new Mock<IVerificationQueueService>();
        queue.Setup(q => q.ProposeVideoCallAsync(bookingId, It.IsAny<HostScope>(), MeetingUrl, Operator, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(Operator, It.IsAny<IReadOnlySet<string>>(), _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(OrgScope);
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_orgId);
        var controller = new VerificationController(queue.Object, scopes.Object, orgs.Object, NullLogger<VerificationController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Operator)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

        var result = await controller.ProposeVideoCall(bookingId, new VideoCallRequest { Url = MeetingUrl }, Ct);

        if (code is null)
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal("sent", Assert.IsType<VideoCallProposedDto>(ok.Value).Status);
            return;
        }

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(code, Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    [Fact]
    public void Endpoint_IsUnderThePolicyOfTheOperators_AndBehindTheFlag()
    {
        var method = typeof(VerificationController).GetMethod(nameof(VerificationController.ProposeVideoCall))!;

        Assert.Equal([CasazenPolicies.VerificationDecision], typeof(VerificationController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != CasazenPolicies.VerificationDecision);
        Assert.Equal([FeatureFlags.CheckInIdentityVerification], method.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag));
        Assert.Equal("{bookingId:guid}/video-call", method.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    /// <summary>The e-mail exists in the five languages of the guest and every value in it is HTML-encoded, the address of the meeting too.</summary>
    [Fact]
    public void Email_IsInFiveLanguages_AndEveryValueIsEncoded()
    {
        var subjects = new List<string>();
        foreach (var name in new[] { "it-IT", "en", "de", "fr", "es" })
        {
            var culture = CultureInfo.GetCultureInfo(name);
            var content = EmailTemplates.GuestVideoCall(culture, "Anna", "Villa Rosa", new DateTime(2026, 11, 20), MeetingUrl);

            Assert.Contains($"<html lang=\"{culture.TwoLetterISOLanguageName}\">", content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
            Assert.Contains("Villa Rosa", content.Subject, StringComparison.Ordinal);
            Assert.Contains($"href=\"{MeetingUrl}\"", content.HtmlBody, StringComparison.Ordinal);
            subjects.Add(content.Subject);
        }

        Assert.Equal(5, subjects.Distinct(StringComparer.Ordinal).Count());

        var hostile = EmailTemplates.GuestVideoCall(
            CultureInfo.GetCultureInfo("en"),
            "<b>Anna</b>",
            "<i>Villa</i>",
            new DateTime(2026, 11, 20),
            "https://meet.example.test/a?x=1&y=2\"onmouseover=\"alert(1)");
        Assert.DoesNotContain("<b>Anna</b>", hostile.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>Villa</i>", hostile.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"onmouseover=\"", hostile.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&amp;y=2", hostile.HtmlBody, StringComparison.Ordinal);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private VerificationQueueService Service() => new(
        _db,
        _queue.Object,
        new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
        Mock.Of<IGuestCheckInService>(),
        new ListLogger<VerificationQueueService>(_logs),
        _clock,
        Mock.Of<IAccessCodeService>());

    /// <summary>A confirmed stay of the property whose check-in session was handed to an operator (not accepted), with the open "To do" of the verification.</summary>
    private async Task<Guid> AddStayAsync(
        string? email,
        string? language = "it",
        BookingStatus status = BookingStatus.Confirmed,
        BiometricOutcome outcome = BiometricOutcome.Escalate)
    {
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = _orgId, FirstName = "Anna", LastName = "Bianchi", Email = email ?? string.Empty };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            PropertyId = _propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 20),
            CheckOutDate = new DateTime(2026, 11, 22),
            Status = status,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        _db.Guests.Add(guest);
        _db.Bookings.Add(booking);
        _db.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = _orgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = Now.UtcDateTime.AddDays(10),
            Status = GuestCheckInSessionStatus.InCompilazione,
            Language = language,
            BiometricOutcome = outcome,
            OperatorRequestedAt = Now.UtcDateTime.AddHours(-1),
            CreatedAt = Now.UtcDateTime.AddHours(-2),
            UpdatedAt = Now.UtcDateTime.AddHours(-1),
        });
        _db.InternalActivities.Add(new InternalActivity
        {
            OrgId = _orgId,
            PropertyId = _propertyId,
            ActivityType = nameof(ActivityType.IdentityVerification),
            Action = ActivityAction.VerifyIdentity,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = booking.Id,
            Urgency = ActivityUrgency.High,
            Status = InternalActivity.StatusPending,
            Reason = EmailTexts.Get("TodoReason_IdentityVerification", EmailTemplates.DefaultCulture),
            ScheduledAt = Now.UtcDateTime,
            CreatedAt = Now.UtcDateTime.AddHours(-1),
        });
        await _db.SaveChangesAsync(Ct);
        return booking.Id;
    }

    /// <summary>Reads every column of every table of the model: no value holds <paramref name="text"/>.</summary>
    private void AssertNoRowHolds(string text)
    {
        _db.ChangeTracker.Clear();
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
        var holders = new List<string>();
        foreach (var entityType in _db.Model.GetEntityTypes().Where(e => !e.IsOwned() && !e.HasSharedClrType && e.FindPrimaryKey() is not null))
        {
            var rows = ((System.Collections.IEnumerable)set.MakeGenericMethod(entityType.ClrType).Invoke(_db, null)!).Cast<object>().ToList();
            foreach (var row in rows)
            {
                foreach (var property in _db.Entry(row).Properties)
                {
                    if (property.CurrentValue is string value && value.Contains(text, StringComparison.Ordinal))
                        holders.Add($"{entityType.DisplayName()}.{property.Metadata.Name}");
                }
            }
        }

        Assert.True(holders.Count == 0, $"'{text}' is stored in: {string.Join(", ", holders)}");
    }

    /// <summary>A logger that keeps every formatted line, to prove what is (not) written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
