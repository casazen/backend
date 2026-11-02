using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The guest completes the online check-in on the public page and the host is told: the whole chain over the real pipeline, from the
/// <c>POST /api/public/checkin/{token}</c> of the guest to the entry the host reads in the bell (<c>GET /api/me/notifications</c>),
/// through the push queued by the service, the decorator and the notification job. The live message of the open web sessions is
/// published by that job (<c>InAppNotificationJobPublishTests</c>); this proves the check-in reaches it at all, which it did not.
/// </summary>
public class GuestCheckInNotificationIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public GuestCheckInNotificationIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GuestSubmitsTheCheckIn_TheHostGetsOneEntryOfTheBell_ForThatBooking()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var (token, sessionId) = await IssueLinkAsync(seed.BookingId);
        using var guestClient = _factory.CreateClient();

        var response = await guestClient.PostAsync($"/api/public/checkin/{token}", SubmitContent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The request queued the push and, next to it, the job of the bell: nothing else runs inside it.
        var key = PushDeliveryKeys.GuestCheckInCompleted(sessionId);
        var jobs = QueuedJobs(key);
        Assert.Equal([typeof(PushDeliveryJob), typeof(InAppNotificationJob)], jobs.Select(j => j.Type));

        // The worker runs the notification job, and once more (a Hangfire retry): still one entry.
        var notification = jobs.Single(j => j.Type == typeof(InAppNotificationJob));
        for (var run = 0; run < 2; run++)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InAppNotificationJob>()
                .CreateAsync((string)notification.Args[0], (QueuedInAppNotification)notification.Args[1], CancellationToken.None);
        }

        using var host = _factory.CreateAuthenticatedClient(seed.OwnerId, "PropertyOwner");
        var list = await host.GetFromJsonAsync<JsonElement>("/api/me/notifications");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(PushTypes.GuestCheckInCompleted, item.GetProperty("type").GetString());
        Assert.Equal(seed.BookingId, item.GetProperty("entityId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
        Assert.Equal(1, (await host.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count")).GetProperty("count").GetInt32());
        // Neither the push text nor anything the guest typed is in the answer.
        foreach (var typed in new[] { "Luigi", "Verdi", "YA1234567", "check-in online" })
            Assert.DoesNotContain(typed, list.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GuestSubmitsDataTheFormRefuses_NothingIsQueuedForTheHost()
    {
        var seed = await _factory.SeedConfirmedBookingWithTokenAsync();
        var (token, sessionId) = await IssueLinkAsync(seed.BookingId);
        using var guestClient = _factory.CreateClient();

        var response = await guestClient.PostAsync($"/api/public/checkin/{token}", SubmitContent(documentType: "AlienPermit"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(QueuedJobs(PushDeliveryKeys.GuestCheckInCompleted(sessionId)));
    }

    // ─── Helpers ───

    private async Task<(string Token, Guid SessionId)> IssueLinkAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.FindAsync(bookingId);
        var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        var issued = await service.IssueLinkAsync(bookingId, booking!.OrgId);
        return (issued.Token, issued.SessionId);
    }

    private List<Job> QueuedJobs(string deliveryKey) =>
        _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == nameof(Hangfire.IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Args.Count > 0 && j.Args[0] is string key && key == deliveryKey)
            .ToList();

    private static StringContent SubmitContent(string documentType = "Passport") =>
        new($$"""
        {
          "guests": [
            {
              "type": "SingleGuest",
              "firstName": "Luigi",
              "lastName": "Verdi",
              "gender": "Male",
              "dateOfBirth": "1990-05-15",
              "bornInItaly": true,
              "birthComuneName": "Roma",
              "birthProvince": "RM",
              "citizenshipName": "Italia",
              "documentType": "{{documentType}}",
              "documentNumber": "YA1234567",
              "documentIssuePlaceName": "Roma"
            }
          ],
          "marketingConsent": false
        }
        """,
        Encoding.UTF8,
        "application/json");
}
