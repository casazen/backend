using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Push;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// MO-04 (A6-08, A6-29) over the real pipeline on PostgreSQL: a new service request pushes the supplier, a rejection
/// pushes the host, the controllers answer without calling Expo (the pushes are Hangfire jobs), and a job run again
/// by Hangfire sends nothing twice.
/// </summary>
public class PushNotificationsPostgresTests : IClassFixture<PushNotificationsPostgresTests.PushFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PushFactory _factory;

    public PushNotificationsPostgresTests(PushFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task CreateAndReject_ControllersAnswerWithoutWaitingForExpo_ThenTheJobsPushSupplierAndHostOnce()
    {
        var w = await SeedWorldAsync();
        // A send made inside a request would block it until the test times out.
        var expoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Expo.SendGate = expoGate.Task;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, "PropertyOwner");
        var create = await host.PostAsJsonAsync(
            "/api/service-requests",
            new { propertyId = w.PropertyId, bookingId = w.BookingId, supplierOrgId = w.SupplierOrgId, category = "cleaning" },
            timeout.Token);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var id = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync(), JsonOptions).GetProperty("id").GetGuid();

        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, "Supplier");
        var reject = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non disponibile" }, timeout.Token);
        Assert.True(reject.StatusCode == HttpStatusCode.OK, await reject.Content.ReadAsStringAsync());

        // Both answered while Expo was unreachable: nothing was sent inside the requests, two jobs were queued.
        Assert.Empty(_factory.Expo.SendRequests);
        var jobs = QueuedPushJobs()
            .Where(j => ((string)j.Args[0]).Contains(id.ToString("N"), StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            [PushDeliveryKeys.ServiceRequestCreated(id), PushDeliveryKeys.ServiceRequestStatus(id, ServiceRequestStatus.Rifiutato)],
            jobs.Select(j => (string)j.Args[0]));

        // The Hangfire worker runs them later, and once more (a retry): one message per device and event.
        expoGate.SetResult();
        foreach (var job in jobs.Concat(jobs))
            await RunAsync(job);

        var messages = _factory.Expo.Messages.Where(m => m.Data.GetValueOrDefault("serviceRequestId") == id.ToString()).ToList();
        Assert.Equal(2, messages.Count);
        var toSupplier = Assert.Single(messages, m => m.Data["type"] == PushTypes.ServiceRequestCreated);
        Assert.Equal(w.SupplierToken, toSupplier.To);
        var toHost = Assert.Single(messages, m => m.Data["type"] == PushTypes.ServiceRequestRejected);
        Assert.Equal(w.HostToken, toHost.To);
        Assert.Equal("Richiesta rifiutata dal fornitore", toHost.Title);
        Assert.Equal(PushRoutes.Booking(w.BookingId), toHost.Data["route"]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deliveries = await db.PushDeliveries.AsNoTracking()
            .Where(d => d.DeliveryKey.Contains(id.ToString("N")))
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d =>
        {
            Assert.Equal(PushDeliveryStatus.Accepted, d.Status);
            Assert.NotNull(d.TicketId);
        });
    }

    [PostgresFact]
    public async Task PushDeliveries_SameKeyAndToken_IsRejectedByTheUniqueIndex()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = $"test:{Guid.NewGuid():N}";
        db.PushDeliveries.Add(new PushDelivery { DeliveryKey = key, PushToken = "ExponentPushToken[x]", Type = "new-booking" });
        await db.SaveChangesAsync();

        db.PushDeliveries.Add(new PushDelivery { DeliveryKey = key, PushToken = "ExponentPushToken[x]", Type = "new-booking" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private IReadOnlyList<Job> QueuedPushJobs() =>
        _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == nameof(Hangfire.IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Type == typeof(PushDeliveryJob))
            .ToList();

    private async Task RunAsync(Job job)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PushDeliveryJob>()
            .SendAsync((string)job.Args[0], (QueuedPush)job.Args[1], CancellationToken.None);
    }

    private sealed record World(
        string OwnerId,
        Guid PropertyId,
        Guid BookingId,
        Guid SupplierOrgId,
        string SupplierUserId,
        string HostToken,
        string SupplierToken);

    /// <summary>
    /// A host org with a property in comune H501, a confirmed stay and the owner's phone; an active supplier operating
    /// in H501 for cleaning, with a member and the member's phone.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        const string comune = "H501";
        var ownerId = $"auth0|mo04-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa MO04",
            Address = $"Via MO04 {Guid.NewGuid():N}",
            City = comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Ospite", Email = $"mo04-{Guid.NewGuid():N}@example.com" };
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        var supplierOrg = new OrgEntity
        {
            Name = "MO04 Supplier",
            Slug = $"mo04-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "MO04 Supplier",
            ContactEmail = $"mo04-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var member = new User
        {
            Id = $"auth0|mo04-supplier-{Guid.NewGuid():N}",
            Email = $"sara-{Guid.NewGuid():N}@example.com",
            FirstName = "Sara",
            LastName = "Fornitore",
            OrgId = supplierOrg.Id,
            SupplierOrgId = supplierOrg.Id,
            IsActive = true,
        };
        var profile = new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = supplierOrg.ContactEmail,
            LegalName = "MO04 Pulizie Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
            TosAcceptedAt = DateTime.UtcNow,
        };
        var hostToken = $"ExponentPushToken[host-{Guid.NewGuid():N}]";
        var supplierToken = $"ExponentPushToken[supplier-{Guid.NewGuid():N}]";
        var devices = new[]
        {
            new DeviceRegistration { UserId = ownerId, OrgId = org.Id, Platform = "ios", PushToken = hostToken, DeviceId = Guid.NewGuid().ToString() },
            new DeviceRegistration { UserId = member.Id, OrgId = supplierOrg.Id, Platform = "android", PushToken = supplierToken, DeviceId = Guid.NewGuid().ToString() },
        };

        db.AddRange(property, guest, stay, supplierOrg, member, profile);
        db.DeviceRegistrations.AddRange(devices);
        await db.SaveChangesAsync();

        return new World(ownerId, property.Id, stay.Id, supplierOrg.Id, member.Id, hostToken, supplierToken);
    }

    /// <summary>The application with the Expo client mocked (Hangfire is already mocked by the base factory).</summary>
    public sealed class PushFactory : CasazenWebApplicationFactory
    {
        public FakeExpoPushClient Expo { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IExpoPushClient>(services);
                services.AddSingleton<IExpoPushClient>(Expo);
            });
        }
    }
}
