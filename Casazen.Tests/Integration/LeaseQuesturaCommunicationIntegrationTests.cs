using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-07 (A7-08) over the real pipeline on PostgreSQL with a fixed clock: the Questura item of the RLI checklist exists
/// only with an extra-EU tenant, stays unticked after CasaZen's reminder email and is ticked only by the landlord's
/// declaration (<c>POST rli/questura/mark-done</c>, receipt in the private bucket); the 48 hours count from the declared
/// delivery date or the start date; another org's lease is not found.
/// </summary>
public class LeaseQuesturaCommunicationIntegrationTests
    : IClassFixture<LeaseQuesturaCommunicationIntegrationTests.QuesturaFactory>
{
    /// <summary>24/9/2026 10:00 UTC. The test leases start on 23/9: delivery by default, the 48 hours end on 25/9.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly byte[] ReceiptPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% ricevuta PEC Questura di test\n%%EOF\n");

    private readonly QuesturaFactory _factory;

    public LeaseQuesturaCommunicationIntegrationTests(QuesturaFactory factory)
    {
        _factory = factory;
        _factory.Clock.SetUtcNow(Now);
        _factory.Email.Result = EmailSendResult.Sent();
    }

    [PostgresFact]
    public async Task Checklist_EuTenant_NoQuesturaItemAndMarkDoneRefused()
    {
        var owner = UniqueOwner("eu");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "DE");

        var checklist = await GetChecklistAsync(client, leaseId);

        Assert.DoesNotContain(ChecklistKeys(checklist), key => key == RliChecklistKeys.QuesturaExtraEu);
        Assert.Equal(JsonValueKind.Null, checklist.GetProperty("questura").ValueKind);
        var markDone = await MarkDoneAsync(client, leaseId, "2026-09-24");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, markDone.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.NotRequired, await ProblemCodeAsync(markDone));
    }

    [PostgresFact]
    public async Task Checklist_ExtraEuTenantAfterReminderEmail_ItemStillNotTicked()
    {
        var owner = UniqueOwner("reminded");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "us");

        await RunReminderJobAsync();

        // The reminder was sent and recorded (delivery 23/9, 48 hours to 25/9)...
        Assert.Contains(
            await ReminderPayloadsAsync(leaseId),
            p => p == $"{RliDeadlineReminderJob.QuesturaThresholds.Delivery}:2026-09-25");
        Assert.Contains(_factory.Email.Sent, e => e.Subject.Contains("Questura", StringComparison.Ordinal));
        // ...but it is not the communication: the item stays to do.
        var checklist = await GetChecklistAsync(client, leaseId);
        var item = ChecklistItem(checklist, RliChecklistKeys.QuesturaExtraEu);
        Assert.False(item.GetProperty("done").GetBoolean());
        var questura = checklist.GetProperty("questura");
        Assert.Equal(new DateTime(2026, 9, 23), questura.GetProperty("deliveryDate").GetDateTime().Date);
        Assert.False(questura.GetProperty("deliveryDateDeclared").GetBoolean());
        Assert.Equal(new DateTime(2026, 9, 25), questura.GetProperty("deadline").GetDateTime().Date);
        Assert.Equal(1, questura.GetProperty("daysRemaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, questura.GetProperty("communicationDate").ValueKind);
    }

    [PostgresFact]
    public async Task MarkDone_WithDateAndReceipt_TicksItemRecordsEventAndStopsReminders()
    {
        var owner = UniqueOwner("mark-done");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "US");

        var response = await MarkDoneAsync(client, leaseId, "2026-09-24", ReceiptPdf);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checklist = await ReadJson(response);
        Assert.True(ChecklistItem(checklist, RliChecklistKeys.QuesturaExtraEu).GetProperty("done").GetBoolean());
        var questura = checklist.GetProperty("questura");
        Assert.Equal(new DateTime(2026, 9, 24), questura.GetProperty("communicationDate").GetDateTime().Date);
        Assert.True(questura.GetProperty("hasReceipt").GetBoolean());
        var lease = await GetLeaseAsync(client, leaseId);
        Assert.Contains(EventTypes(lease), e => e == nameof(LeaseEventType.QuesturaCommunicationMarkedDone));
        // The storage key and the declaring user stay on the server.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("questura/", body, StringComparison.Ordinal);
        Assert.DoesNotContain(owner, body, StringComparison.Ordinal);

        var receipt = await client.GetAsync($"/api/leases/{leaseId}/rli/questura/receipt");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("application/pdf", receipt.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ReceiptPdf, await receipt.Content.ReadAsByteArrayAsync());

        var again = await MarkDoneAsync(client, leaseId, "2026-09-23");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.AlreadyMarkedDone, await ProblemCodeAsync(again));

        // Once declared, no Questura reminder anymore (the day after the deadline would be "overdue").
        _factory.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero));
        await RunReminderJobAsync();
        Assert.DoesNotContain(await ReminderPayloadsAsync(leaseId), p => p!.StartsWith("questura-", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task MarkDone_WithoutReceipt_TickedAndReceiptNotAvailable()
    {
        var owner = UniqueOwner("no-receipt");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "CN");

        var response = await MarkDoneAsync(client, leaseId, "2026-09-23");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checklist = await ReadJson(response);
        Assert.True(ChecklistItem(checklist, RliChecklistKeys.QuesturaExtraEu).GetProperty("done").GetBoolean());
        Assert.False(checklist.GetProperty("questura").GetProperty("hasReceipt").GetBoolean());
        var receipt = await client.GetAsync($"/api/leases/{leaseId}/rli/questura/receipt");
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.ReceiptNotAvailable, await ProblemCodeAsync(receipt));
    }

    [PostgresFact]
    public async Task MarkDone_DateAfterTodayOrReceiptNotAPdf_Returns422AndChangesNothing()
    {
        var owner = UniqueOwner("mark-invalid");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "US");

        var future = await MarkDoneAsync(client, leaseId, "2026-09-25");
        var notPdf = await MarkDoneAsync(client, leaseId, "2026-09-24", Encoding.ASCII.GetBytes("non sono un pdf"));
        using var missingDate = new MultipartFormDataContent { { new StringContent("x"), "note" } };
        var noDate = await client.PostAsync($"/api/leases/{leaseId}/rli/questura/mark-done", missingDate);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.DateInFuture, await ProblemCodeAsync(future));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notPdf.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.ReceiptInvalid, await ProblemCodeAsync(notPdf));
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
        var checklist = await GetChecklistAsync(client, leaseId);
        Assert.False(ChecklistItem(checklist, RliChecklistKeys.QuesturaExtraEu).GetProperty("done").GetBoolean());
        Assert.Empty(StoredQuesturaReceiptsOf(leaseId));
    }

    [PostgresFact]
    public async Task DeliveryDate_DeclaredClearedOrAfterEnd_DeadlineFollowsIt()
    {
        var owner = UniqueOwner("delivery");
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(client, property.Id, tenantCitizenship: "US");

        var declared = await client.PutAsJsonAsync(
            $"/api/leases/{leaseId}/rli/questura/delivery-date", new { deliveryDate = "2026-09-28" });

        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        var questura = (await ReadJson(declared)).GetProperty("questura");
        Assert.True(questura.GetProperty("deliveryDateDeclared").GetBoolean());
        Assert.Equal(new DateTime(2026, 9, 28), questura.GetProperty("deliveryDate").GetDateTime().Date);
        Assert.Equal(new DateTime(2026, 9, 30), questura.GetProperty("deadline").GetDateTime().Date);
        Assert.Equal(6, questura.GetProperty("daysRemaining").GetInt32());
        Assert.Contains(EventTypes(await GetLeaseAsync(client, leaseId)), e => e == nameof(LeaseEventType.PropertyDeliveryDateDeclared));

        var afterEnd = await client.PutAsJsonAsync(
            $"/api/leases/{leaseId}/rli/questura/delivery-date", new { deliveryDate = "2031-01-01" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, afterEnd.StatusCode);
        Assert.Equal(QuesturaCommunicationErrorCodes.DeliveryDateAfterEnd, await ProblemCodeAsync(afterEnd));

        var cleared = await client.PutAsJsonAsync(
            $"/api/leases/{leaseId}/rli/questura/delivery-date", new { deliveryDate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var back = (await ReadJson(cleared)).GetProperty("questura");
        Assert.False(back.GetProperty("deliveryDateDeclared").GetBoolean());
        Assert.Equal(new DateTime(2026, 9, 23), back.GetProperty("deliveryDate").GetDateTime().Date);
    }

    [PostgresFact]
    public async Task QuesturaEndpoints_LeaseOfAnotherOrg_Return404AndChangeNothing()
    {
        var owner = UniqueOwner("org-a");
        var outsider = UniqueOwner("org-b");
        var property = await _factory.SeedPropertyAsync(owner);
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var ownerClient = LandlordClient(owner);
        var leaseId = await DriveToSignedAsync(ownerClient, property.Id, tenantCitizenship: "US");
        (await MarkDoneAsync(ownerClient, leaseId, "2026-09-24", ReceiptPdf)).EnsureSuccessStatusCode();
        var otherLeaseId = await DriveToSignedAsync(ownerClient, property.Id, tenantCitizenship: "US");

        using var outsiderClient = LandlordClient(outsider);
        var markDone = await MarkDoneAsync(outsiderClient, otherLeaseId, "2026-09-24", ReceiptPdf);
        var delivery = await outsiderClient.PutAsJsonAsync(
            $"/api/leases/{otherLeaseId}/rli/questura/delivery-date", new { deliveryDate = "2026-09-28" });
        var receipt = await outsiderClient.GetAsync($"/api/leases/{leaseId}/rli/questura/receipt");

        Assert.Equal(HttpStatusCode.NotFound, markDone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delivery.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        Assert.DoesNotContain("%PDF", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var questura = (await GetChecklistAsync(ownerClient, otherLeaseId)).GetProperty("questura");
        Assert.Equal(JsonValueKind.Null, questura.GetProperty("communicationDate").ValueKind);
        Assert.False(questura.GetProperty("deliveryDateDeclared").GetBoolean());
        Assert.Empty(StoredQuesturaReceiptsOf(otherLeaseId));

        // Without lease.register (no long-term role) the declaration is forbidden, anonymous is unauthorized.
        using var noLeaseRole = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        Assert.Equal(HttpStatusCode.Forbidden, (await MarkDoneAsync(noLeaseRole, otherLeaseId, "2026-09-24")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/leases/{leaseId}/rli/questura/receipt")).StatusCode);
    }

    private async Task RunReminderJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RliDeadlineReminderJob>().ExecuteAsync();
    }

    private async Task<List<string?>> ReminderPayloadsAsync(Guid leaseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LeaseEvents.AsNoTracking()
            .Where(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.DeadlineReminderSent)
            .Select(e => e.Payload)
            .ToListAsync();
    }

    /// <summary>Questura receipts of the lease in the private bucket of the test storage.</summary>
    private string[] StoredQuesturaReceiptsOf(Guid leaseId)
    {
        var folder = Directory.EnumerateDirectories(_factory.StorageRoot, leaseId.ToString(), SearchOption.AllDirectories)
            .Select(d => Path.Combine(d, "questura"))
            .FirstOrDefault(Directory.Exists);
        return folder is null ? [] : Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
    }

    private static async Task<HttpResponseMessage> MarkDoneAsync(
        HttpClient client, Guid leaseId, string communicationDate, byte[]? receipt = null)
    {
        using var form = new MultipartFormDataContent { { new StringContent(communicationDate), "communicationDate" } };
        if (receipt is not null)
        {
            var file = new ByteArrayContent(receipt);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "receipt", "ricevuta-questura.pdf");
        }

        return await client.PostAsync($"/api/leases/{leaseId}/rli/questura/mark-done", form);
    }

    private static async Task<Guid> DriveToSignedAsync(HttpClient client, Guid propertyId, string tenantCitizenship)
    {
        var create = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId,
            contractType = "Libero",
            taxRegime = "CedolareSecca",
            startDate = "2026-09-23T00:00:00Z",
            endDate = "2030-09-22T00:00:00Z",
            monthlyRent = 900m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "John", lastName = "Smith", fiscalCode = "SMTJHN85B02Z404X", citizenship = tenantCitizenship, contactEmail = "john@example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var leaseId = (await ReadJson(create)).GetProperty("id").GetGuid();
        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId)).EnsureSuccessStatusCode();
        return leaseId;
    }

    private static async Task<JsonElement> GetChecklistAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}/rli/checklist");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static async Task<JsonElement> GetLeaseAsync(HttpClient client, Guid leaseId)
    {
        var response = await client.GetAsync($"/api/leases/{leaseId}");
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static IEnumerable<string?> ChecklistKeys(JsonElement checklist) =>
        checklist.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();

    private static JsonElement ChecklistItem(JsonElement checklist, string key) =>
        checklist.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    private static IEnumerable<string?> EventTypes(JsonElement lease) =>
        lease.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await ReadJson(response)).GetProperty("code").GetString();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);

    private HttpClient LandlordClient(string ownerId) => _factory.CreateAuthenticatedClient(ownerId, "LongTermLandlord");

    private static string UniqueOwner(string suffix) => $"auth0|lt07-{suffix}-{Guid.NewGuid():N}";

    /// <summary>Lease flow factory with a clock the tests control and an email provider that records what it sends.</summary>
    public sealed class QuesturaFactory : LeaseFlowWebApplicationFactory
    {
        public FakeTimeProvider Clock { get; } = new(Now);

        internal RecordingEmailService Email { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<TimeProvider>(services);
                services.AddSingleton<TimeProvider>(Clock);
                RemoveAllOf<IEmailService>(services);
                services.AddSingleton<IEmailService>(Email);
            });
        }
    }

    internal sealed class RecordingEmailService : IEmailService
    {
        public EmailSendResult Result { get; set; } = EmailSendResult.Sent();

        public ConcurrentQueue<(string To, string Subject)> Sent { get; } = new();

        public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
        {
            Sent.Enqueue((to, subject));
            return Task.FromResult(Result);
        }
    }
}
