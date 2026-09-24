using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-09 (A5-26, A5-27) on real PostgreSQL: the host always gets a check-in link to copy and sees the real outcome of its
/// email, expired links are replaced by the daily job, the host can enter the guest data himself with the same checks as
/// the guest portal, and the guest's document scan is downloadable only by the guest's org.
/// </summary>
public sealed class CheckInHostFallbackPostgresTests : IClassFixture<CheckInHostFallbackPostgresTests.FallbackFactory>, IDisposable
{
    private const string OwnerRole = "PropertyOwner";
    private const string PublicSite = "https://casazen-app.vercel.app";

    private readonly FallbackFactory _factory;
    private readonly List<IServiceScope> _scopes = [];

    public CheckInHostFallbackPostgresTests(FallbackFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task ResendLink_EmailRejectedByProvider_LinkStaysUsableCopyableAndReminderPossible()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var sent = await host.PostAsync($"/api/bookings/{seed.BookingId}/checkin/resend-link", null);

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var (link, emailStatus, _) = await ReadLinkAsync(sent);
        Assert.StartsWith($"{PublicSite}/checkin/", link);
        Assert.Equal("Queued", emailStatus);
        var token = link[($"{PublicSite}/checkin/".Length)..];

        // The provider refuses the address: the email job records it, the link is not expired.
        _factory.Email.Result = new EmailSendResult(false, "invalid recipient");
        await RunQueuedLinkEmailAsync(token);

        using (var session = await GetSessionAsync(host, seed.BookingId))
        {
            var root = session.RootElement;
            Assert.Equal("Inviato", root.GetProperty("status").GetString());
            Assert.Equal("Failed", root.GetProperty("emailStatus").GetString());
            Assert.Equal(GuestCheckInLinkEmailErrors.Rejected, root.GetProperty("emailError").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("sentAt").ValueKind);
            Assert.True(root.GetProperty("canIssueLink").GetBoolean());
        }

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/public/checkin/{token}")).StatusCode);

        // The host copies a new link (no email): the previous one stops working, the new one opens the portal.
        var copied = await host.PostAsync($"/api/bookings/{seed.BookingId}/checkin/link", null);
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        var (copyLink, copyStatus, _) = await ReadLinkAsync(copied);
        Assert.Equal("NotRequested", copyStatus);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/public/checkin/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(copyLink.Replace(PublicSite, "/api/public", StringComparison.Ordinal))).StatusCode);

        // Reminder by email, once the address is corrected: sent for real this time.
        _factory.Email.Result = EmailSendResult.Sent();
        var reminder = await host.PostAsync($"/api/bookings/{seed.BookingId}/checkin/resend-link", null);
        Assert.Equal(HttpStatusCode.OK, reminder.StatusCode);
        var (reminderLink, reminderStatus, _) = await ReadLinkAsync(reminder);
        Assert.Equal("Queued", reminderStatus);
        await RunQueuedLinkEmailAsync(reminderLink[($"{PublicSite}/checkin/".Length)..]);
        using var after = await GetSessionAsync(host, seed.BookingId);
        Assert.Equal("Sent", after.RootElement.GetProperty("emailStatus").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.RootElement.GetProperty("sentAt").ValueKind);
    }

    [PostgresFact]
    public async Task ResendLink_GuestWithoutEmail_ReturnsTheLinkWithFailedEmail()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        await UpdateAsync<Guest>(seed.GuestId, g => g.Email = string.Empty);
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var sent = await host.PostAsync($"/api/bookings/{seed.BookingId}/checkin/resend-link", null);

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var (link, emailStatus, emailError) = await ReadLinkAsync(sent);
        Assert.StartsWith($"{PublicSite}/checkin/", link);
        Assert.Equal("Failed", emailStatus);
        Assert.Equal(GuestCheckInLinkEmailErrors.NoRecipient, emailError);
    }

    [PostgresFact]
    public async Task SendJob_LinkExpiredByTime_ShowsExpiredThenIssuesANewLink()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var staleId = await AddSessionAsync(seed.BookingId, GuestCheckInSessionStatus.InCompilazione, expiresAt: DateTime.UtcNow.AddMinutes(-5));
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        using (var before = await GetSessionAsync(host, seed.BookingId))
        {
            Assert.Equal(staleId, before.RootElement.GetProperty("sessionId").GetGuid());
            Assert.Equal("Scaduto", before.RootElement.GetProperty("status").GetString());
            Assert.True(before.RootElement.GetProperty("canIssueLink").GetBoolean());
        }

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<GuestCheckInSendJob>().ExecuteAsync();

        using var db = NewDb();
        var sessions = await db.GuestCheckInSessions.Where(s => s.BookingId == seed.BookingId).OrderBy(s => s.CreatedAt).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, sessions[0].Status);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, sessions[1].Status);
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, sessions[1].LinkEmailStatus);
        Assert.True(sessions[1].ExpiresAt > DateTime.UtcNow.AddDays(6));
        Assert.Contains(LinkEmailJobs(), job => (Guid)job.Args[0] == sessions[1].Id);

        using var after = await GetSessionAsync(host, seed.BookingId);
        Assert.Equal(sessions[1].Id, after.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("Inviato", after.RootElement.GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task HostEntersGuestData_ValidFamily_StayCompleteForAlloggiatiWithHostAsAuthor()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var saved = await host.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new
        {
            guests = new object[]
            {
                Guest("HeadOfFamily", "Luigi", "Male", "1980-02-01", withDocument: true),
                Guest("FamilyMember", "Anna", "Female", "2012-06-15"),
            },
        }));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var summary = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
        {
            var root = summary.RootElement;
            Assert.True(root.GetProperty("dataComplete").GetBoolean());
            var rows = root.GetProperty("guests").EnumerateArray().ToList();
            Assert.All(rows, r => Assert.Equal("Host", r.GetProperty("dataSource").GetString()));
            Assert.All(rows, r => Assert.Equal(0, r.GetProperty("missingFields").GetArrayLength()));
            // Masked like on the guest portal (CO-02): the full number needs an explicit request.
            Assert.Equal("*****567", rows[0].GetProperty("documentNumberMasked").GetString());
            Assert.False(rows[0].TryGetProperty("documentNumber", out _));
        }

        var status = await host.GetFromJsonAsync<JsonElement>($"/api/alloggiati/{seed.BookingId}/status");
        Assert.True(status.GetProperty("dataComplete").GetBoolean());

        using var db = NewDb();
        var stored = await db.StayGuests.Where(s => s.BookingId == seed.BookingId).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.Equal(StayGuestDataSource.Host, s.DataSource));
        Assert.All(stored, s => Assert.Equal(seed.OwnerId, s.EnteredByUserId));
        // Like the guest portal: the communication is scheduled for the arrival day.
        var report = await db.AlloggiatiWebReports.SingleAsync(r => r.BookingId == seed.BookingId);
        Assert.Equal(AlloggiatiWebStatus.DaInviare, report.Status);
        Assert.False(string.IsNullOrEmpty(report.ScheduledJobId));

        // No link to the guest any more: the data is complete.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<GuestCheckInSendJob>().ExecuteAsync();
        Assert.False(await db.GuestCheckInSessions.AnyAsync(s => s.BookingId == seed.BookingId));

        var numbers = await host.GetFromJsonAsync<JsonElement>($"/api/alloggiati/{seed.BookingId}/stay-guests/document-numbers?position=0");
        var number = Assert.Single(numbers.EnumerateArray().ToList());
        Assert.Equal(0, number.GetProperty("position").GetInt32());
        Assert.Equal("YA1234567", number.GetProperty("documentNumber").GetString());
    }

    [PostgresFact]
    public async Task HostAndGuestPortal_SameInvalidGuests_SameFieldErrorsAndMessages()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var token = await IssueTokenAsync(seed.BookingId);
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);
        var future = DateTime.UtcNow.Date.AddYears(1).ToString("yyyy-MM-dd");
        object[] invalidGuests =
        [
            new
            {
                type = "HeadOfFamily",
                firstName = "Luigi",
                lastName = "Verdi",
                gender = "Other",
                dateOfBirth = future,
                bornInItaly = true,
                birthComuneName = "Milano",
                birthProvince = "M1",
                citizenshipName = "Italia",
                documentType = "Other",
                documentNumber = "AB-12/3",
                documentIssuePlaceName = "Milano",
            },
        ];

        var byHost = await host.PutAsync($"/api/alloggiati/{seed.BookingId}/stay-guests", Json(new { guests = invalidGuests }));
        var byGuest = await _factory.CreateClient().PostAsync(
            $"/api/public/checkin/{token}", Json(new { guests = invalidGuests, gdprConsent = true }));

        Assert.Equal(HttpStatusCode.BadRequest, byHost.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, byGuest.StatusCode);
        var hostErrors = await ErrorsAsync(byHost);
        var guestErrors = await ErrorsAsync(byGuest);
        Assert.Equal(
            new[]
            {
                "Guests[0].BirthProvince", "Guests[0].DateOfBirth", "Guests[0].DocumentNumber", "Guests[0].DocumentType",
                "Guests[0].Gender", "Guests[0].Type",
            },
            hostErrors.Keys.Order());
        Assert.Equal(hostErrors, guestErrors);
        using var db = NewDb();
        Assert.False(await db.StayGuests.AnyAsync(s => s.BookingId == seed.BookingId));
    }

    [PostgresFact]
    public async Task CheckInLinkAndGuestData_UserOfAnotherOrg_Gets404AndNothingChanges()
    {
        var seedA = await _factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        var seedB = await _factory.SeedConfirmedBookingWithTokenAsync();
        using var intruder = _factory.CreateAuthenticatedClient(seedB.OwnerId, OwnerRole);
        var booking = seedA.BookingId;

        var responses = new[]
        {
            await intruder.GetAsync($"/api/bookings/{booking}/checkin-session"),
            await intruder.PostAsync($"/api/bookings/{booking}/checkin/link", null),
            await intruder.PostAsync($"/api/bookings/{booking}/checkin/resend-link", null),
            await intruder.GetAsync($"/api/alloggiati/{booking}/stay-guests/document-numbers"),
            await intruder.PutAsync($"/api/alloggiati/{booking}/stay-guests", Json(new
            {
                guests = new object[] { Guest("SingleGuest", "Intruso", "Male", "1970-01-01", withDocument: true) },
            })),
            await intruder.GetAsync($"/api/guests/{seedA.GuestId}/document-scan"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        foreach (var response in responses)
            Assert.DoesNotContain("AB123456", await response.Content.ReadAsStringAsync());
        using var db = NewDb();
        Assert.False(await db.GuestCheckInSessions.AnyAsync(s => s.BookingId == booking));
        Assert.Equal(new[] { "Luigi", "Sofia" }, await db.StayGuests.Where(s => s.BookingId == booking).OrderBy(s => s.Position).Select(s => s.FirstName).ToListAsync());
    }

    [PostgresFact]
    public async Task DownloadDocumentScan_OnlyTheGuestsOrg_GetsThePrivateFile()
    {
        var seedA = await _factory.SeedConfirmedBookingWithTokenAsync();
        var seedB = await _factory.SeedConfirmedBookingWithTokenAsync();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF");
        var orgA = await OrgOfAsync(seedA.BookingId);
        var key = StorageKeys.GuestDocument(orgA, seedA.GuestId, "scan.pdf");
        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await storage.PutAsync(StorageBucket.Private, key, new MemoryStream(bytes), "application/pdf");
        }

        await UpdateAsync<Guest>(seedA.GuestId, g => g.DocumentScanUrl = key);
        using var owner = _factory.CreateAuthenticatedClient(seedA.OwnerId, OwnerRole);
        using var other = _factory.CreateAuthenticatedClient(seedB.OwnerId, OwnerRole);

        var download = await owner.GetAsync($"/api/guests/{seedA.GuestId}/document-scan");
        var byOtherOrg = await other.GetAsync($"/api/guests/{seedA.GuestId}/document-scan");
        var withoutScan = await other.GetAsync($"/api/guests/{seedB.GuestId}/document-scan");
        var anonymous = await _factory.CreateClient().GetAsync($"/api/guests/{seedA.GuestId}/document-scan");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, byOtherOrg.StatusCode);
        Assert.NotEqual(bytes, await byOtherOrg.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, withoutScan.StatusCode);
        Assert.Contains("guest_document_scan_missing", await withoutScan.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [PostgresFact]
    public async Task CreateLink_CompletedCheckIn_Returns409AndKeepsTheSession()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        await AddSessionAsync(seed.BookingId, GuestCheckInSessionStatus.Completo, expiresAt: DateTime.UtcNow.AddDays(3));
        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, OwnerRole);

        var response = await host.PostAsync($"/api/bookings/{seed.BookingId}/checkin/link", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("checkin_already_completed", await response.Content.ReadAsStringAsync());
        using var session = await GetSessionAsync(host, seed.BookingId);
        Assert.Equal("Completo", session.RootElement.GetProperty("status").GetString());
        Assert.False(session.RootElement.GetProperty("canIssueLink").GetBoolean());
    }

    private async Task RunQueuedLinkEmailAsync(string token)
    {
        var hash = GuestCheckInService.HashToken(token);
        using var db = NewDb();
        var sessionId = await db.GuestCheckInSessions.Where(s => s.TokenHash == hash).Select(s => s.Id).SingleAsync();
        var job = Assert.Single(LinkEmailJobs(), j => (Guid)j.Args[0] == sessionId);
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GuestCheckInLinkEmailJob>()
            .SendAsync((Guid)job.Args[0], (string)job.Args[1], (int)job.Args[2]);
    }

    private IReadOnlyList<Job> LinkEmailJobs() =>
        _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == "Create" && i.Arguments[1] is EnqueuedState)
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Type == typeof(GuestCheckInLinkEmailJob))
            .ToList();

    private async Task<string> IssueTokenAsync(Guid bookingId)
    {
        using var db = NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        return (await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance).IssueLinkAsync(bookingId, booking.OrgId)).Token;
    }

    private async Task<Guid> AddSessionAsync(Guid bookingId, GuestCheckInSessionStatus status, DateTime expiresAt)
    {
        using var db = NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        var session = new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = booking.OrgId,
            TokenHash = GuestCheckInService.HashToken(Guid.NewGuid().ToString("N")),
            Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            ExpiresAt = expiresAt,
            CompletedAt = status == GuestCheckInSessionStatus.Completo ? DateTime.UtcNow : null,
        };
        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task UpdateAsync<T>(Guid id, Action<T> change)
        where T : class
    {
        using var db = NewDb();
        var entity = await db.Set<T>().FindAsync(id) ?? throw new InvalidOperationException($"{typeof(T).Name} {id} missing");
        change(entity);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> OrgOfAsync(Guid bookingId)
    {
        using var db = NewDb();
        return await db.Bookings.Where(b => b.Id == bookingId).Select(b => b.OrgId).SingleAsync();
    }

    /// <summary>A context outside any request (no tenant filter, like the background jobs); its scope ends with the test.</summary>
    private AppDbContext NewDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
            scope.Dispose();
    }

    private static async Task<JsonDocument> GetSessionAsync(HttpClient client, Guid bookingId)
    {
        var response = await client.GetAsync($"/api/bookings/{bookingId}/checkin-session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<(string Link, string? EmailStatus, string? EmailError)> ReadLinkAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var error = root.GetProperty("emailError");
        return (
            root.GetProperty("checkInLink").GetString()!,
            root.GetProperty("emailStatus").GetString(),
            error.ValueKind == JsonValueKind.Null ? null : error.GetString());
    }

    private static async Task<Dictionary<string, string>> ErrorsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => string.Join(" | ", p.Value.EnumerateArray().Select(m => m.GetString())));
    }

    private static object Guest(string type, string firstName, string gender, string dateOfBirth, bool withDocument = false) => new
    {
        type,
        firstName,
        lastName = "Verdi",
        gender,
        dateOfBirth,
        bornInItaly = true,
        birthComuneName = "Milano",
        birthProvince = "MI",
        citizenshipName = "Italia",
        documentType = withDocument ? "Passport" : null,
        documentNumber = withDocument ? "YA1234567" : null,
        documentIssuePlaceName = withDocument ? "Milano" : null,
    };

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>An email provider that is configured, with an outcome the test chooses (sent by default).</summary>
    public sealed class FallbackFactory : CasazenWebApplicationFactory
    {
        internal ControllableEmailService Email { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "Resend",
                ["Email:ApiKey"] = "re_test_integration",
                ["Email:FromAddress"] = "noreply@casazen.test",
            }));
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailService>(services);
                services.AddSingleton<IEmailService>(Email);
            });
        }
    }

    internal sealed class ControllableEmailService : IEmailService
    {
        public EmailSendResult Result { get; set; } = EmailSendResult.Sent();

        public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent) => Task.FromResult(Result);
    }
}
