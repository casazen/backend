using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-17 (A5-24) on real PostgreSQL, through the whole pipeline (auth, TN-3, ProblemDetails): the check-out wizard has
/// 5 steps whose answers are saved as progress and recorded at the completion. The cleaning request is created for the
/// stay (SU-07), the tourist tax collection and the property readiness are stored as declared (never a fixed
/// <c>propertyReady: true</c>), the completion needs the start, and the cockpit counts the turnovers still open.
/// The clock of the app is fixed (FD-06): the stays depart "today", 24/09/2026 in Rome.
/// </summary>
public class CheckoutWizardPostgresTests : IClassFixture<CheckoutWizardPostgresTests.Factory>
{
    private const string HostRole = "PropertyOwner";

    /// <summary>12:00 in Rome on 24/09/2026.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTime Today = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    private readonly Factory _factory;

    public CheckoutWizardPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task CheckoutWizard_FullFlow_CreatesTheCleaningRequestForTheStayAndRecordsTaxAndPropertyReady()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property, touristTax: 12m);
        var supplierOrgId = await SeedSupplierAsync(property.City);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var start = await host.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        var startBody = await start.Content.ReadFromJsonAsync<JsonElement>();
        var progress = await host.PutAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/progress", new
        {
            currentStep = "tourist-tax",
            departureConfirmed = true,
            cleaningChoice = "Request",
            supplierOrgId,
            serviceCategory = "cleaning",
            serviceNotes = "Cambio biancheria",
        });
        var complete = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new
        {
            confirmDeparture = true,
            cleaningChoice = "Request",
            supplierOrgId,
            serviceCategory = "cleaning",
            serviceNotes = "Cambio biancheria",
            touristTaxCollection = "CollectedAtProperty",
            propertyReady = true,
            propertyNotes = "Tutto in ordine",
        });

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(
            new[] { "stay-summary", "alloggiati", "cleaning", "tourist-tax", "property-ready" },
            startBody.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(12m, startBody.GetProperty("touristTax").GetProperty("recordedAmount").GetDecimal());
        Assert.Equal("DaInviareManualmente", startBody.GetProperty("alloggiati").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal("tourist-tax", (await progress.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentStep").GetString());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("propertyReady").GetBoolean());
        Assert.Equal("CheckedOut", body.GetProperty("bookingStatus").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The request of step 3, tied to the stay (SU-07) and sent to the chosen supplier.
        var request = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.BookingId == bookingId);
        Assert.Equal(supplierOrgId, request.SupplierOrgId);
        Assert.Equal(property.Id, request.PropertyId);
        Assert.Equal(property.OrgId, request.OrgId);
        Assert.Equal("cleaning", request.Category);
        Assert.Equal("Cambio biancheria", request.Notes);
        Assert.Equal(ServiceRequestRentalContext.ShortRent, request.RentalContext);
        Assert.Equal(request.Id, body.GetProperty("serviceRequestId").GetGuid());
        var record = await db.StayCheckouts.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.BookingId == bookingId);
        Assert.Equal(request.Id, record.CleaningRequestId);
        Assert.Equal(CheckoutCleaningChoice.Request, record.CleaningChoice);
        Assert.Equal(TouristTaxCollection.CollectedAtProperty, record.TouristTaxCollection);
        Assert.True(record.PropertyReady);
        Assert.NotNull(record.PropertyReadyAt);
        Assert.NotNull(record.CompletedAt);
        Assert.Equal("Tutto in ordine", record.PropertyNotes);
        Assert.Equal(property.OrgId, record.OrgId);
        Assert.Equal(BookingStatus.CheckedOut, (await LoadAsync(bookingId)).Status);

        var summary = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");
        Assert.DoesNotContain(bookingId, ItemIds(summary, "turnoversPending"));
        Assert.DoesNotContain(bookingId, ItemIds(summary, "checkoutsDue"));
    }

    [PostgresFact]
    public async Task CheckoutWizard_SkipCleaningPropertyNotReady_NoRequestAndATurnoverUntilTheHostDeclaresItReady()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        await host.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        var complete = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new
        {
            confirmDeparture = true,
            cleaningChoice = "Skip",
            touristTaxCollection = "NotDue",
            propertyReady = false,
            propertyNotes = "Manca una lampadina",
        });
        var turnoverBefore = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");
        var ready = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/property-ready", new { notes = "Lampadina cambiata" });
        var readyAgain = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/property-ready", new { notes = "Altro" });
        var turnoverAfter = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("propertyReady").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("serviceRequestId").ValueKind);
        Assert.Equal("Skip", body.GetProperty("wizard").GetProperty("cleaning").GetProperty("choice").GetString());

        // "Check-out completato ma property non pronta": a turnover of the cockpit, with the action to close it.
        var turnover = Assert.Single(
            turnoverBefore.GetProperty("turnoversPending").GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetGuid() == bookingId);
        Assert.Equal("ConfirmPropertyReady", turnover.GetProperty("action").GetString());
        Assert.Equal(bookingId, turnover.GetProperty("bookingId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readyAgain.StatusCode);
        Assert.DoesNotContain(bookingId, ItemIds(turnoverAfter, "turnoversPending"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.ServiceRequests.IgnoreQueryFilters().AnyAsync(r => r.BookingId == bookingId));
        var record = await db.StayCheckouts.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.BookingId == bookingId);
        Assert.Equal(CheckoutCleaningChoice.Skip, record.CleaningChoice);
        Assert.Equal(TouristTaxCollection.NotDue, record.TouristTaxCollection);
        Assert.NotNull(record.PropertyReadyAt);
        // Idempotent: the second declaration keeps the first one.
        Assert.Equal("Lampadina cambiata", record.PropertyNotes);
    }

    [PostgresFact]
    public async Task CheckoutWizard_CompleteWithoutStart_Returns409AndChangesNothing()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property);
        var supplierOrgId = await SeedSupplierAsync(property.City);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var complete = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new
        {
            confirmDeparture = true,
            cleaningChoice = "Request",
            supplierOrgId,
            serviceCategory = "cleaning",
            touristTaxCollection = "CollectedAtProperty",
            propertyReady = true,
        });
        var progress = await host.PutAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/progress", new
        {
            currentStep = "cleaning",
            departureConfirmed = true,
        });

        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        var problem = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("checkout_wizard_not_started", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, progress.StatusCode);
        Assert.Equal("checkout_wizard_not_started", (await progress.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.CheckedIn, stored.Status);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.ServiceRequests.IgnoreQueryFilters().AnyAsync(r => r.BookingId == bookingId));
        Assert.False(await db.StayCheckouts.IgnoreQueryFilters().AnyAsync(c => c.BookingId == bookingId));
    }

    [PostgresFact]
    public async Task CheckoutWizard_TwoCompletionsTogether_ClosesTheStayOnceWithOneCleaningRequest()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property);
        var supplierOrgId = await SeedSupplierAsync(property.City);
        using var web = _factory.CreateAuthenticatedClient(hostId, HostRole);
        using var app = _factory.CreateAuthenticatedClient(hostId, HostRole);
        await web.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        var payload = new
        {
            confirmDeparture = true,
            cleaningChoice = "Request",
            supplierOrgId,
            serviceCategory = "cleaning",
            touristTaxCollection = "CollectedOnline",
            propertyReady = true,
        };

        var responses = await Task.WhenAll(
            web.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", payload),
            app.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", payload));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("booking_already_checked_out", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.BookingId == bookingId));
        Assert.Equal(1, await db.StayCheckouts.IgnoreQueryFilters().CountAsync(c => c.BookingId == bookingId));
    }

    [PostgresFact]
    public async Task CheckoutWizard_ProgressSaved_StartAgainResumesOnTheSavedStep()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property);
        using var web = _factory.CreateAuthenticatedClient(hostId, HostRole);
        using var app = _factory.CreateAuthenticatedClient(hostId, HostRole);

        await web.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        var saved = await web.PutAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/progress", new
        {
            currentStep = "property-ready",
            departureConfirmed = true,
            cleaningChoice = "Skip",
            touristTaxCollection = "NotCollected",
            propertyReady = false,
        });
        var invalidStep = await web.PutAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/progress", new
        {
            currentStep = "payment",
        });
        var reopened = await app.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidStep.StatusCode);
        Assert.Equal("checkout_step_invalid", (await invalidStep.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var body = await reopened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("property-ready", body.GetProperty("currentStep").GetString());
        Assert.True(body.GetProperty("stay").GetProperty("departureConfirmed").GetBoolean());
        Assert.Equal("Skip", body.GetProperty("cleaning").GetProperty("choice").GetString());
        Assert.Equal("NotCollected", body.GetProperty("touristTax").GetProperty("collection").GetString());
        Assert.False(body.GetProperty("propertyReady").GetProperty("ready").GetBoolean());
        Assert.Equal(BookingStatus.CheckedIn, (await LoadAsync(bookingId)).Status);
    }

    [PostgresFact]
    public async Task CheckoutWizard_BookingOfAnotherOrg_Answers404EverywhereAndChangesNothing()
    {
        var (_, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(property);
        var (intruderId, _) = await SeedHostPropertyAsync();
        using var intruder = _factory.CreateAuthenticatedClient(intruderId, HostRole);

        var responses = new[]
        {
            await intruder.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null),
            await intruder.GetAsync($"/api/bookings/{bookingId}/checkout-wizard"),
            await intruder.PutAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/progress", new { currentStep = "cleaning", departureConfirmed = true }),
            await intruder.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new { confirmDeparture = true, cleaningChoice = "Skip", propertyReady = true }),
            await intruder.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/property-ready", new { notes = "x" }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.CheckedIn, stored.Status);
        Assert.Null(stored.CheckoutWizardStartedAt);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StayCheckouts.IgnoreQueryFilters().AnyAsync(c => c.BookingId == bookingId));
    }

    private static IEnumerable<Guid> ItemIds(JsonElement summary, string section) =>
        summary.GetProperty(section).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    /// <summary>A host with its org and an active property in Rome.</summary>
    private async Task<(string HostId, Property Property)> SeedHostPropertyAsync()
    {
        var hostId = $"auth0|co17-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        return (hostId, property);
    }

    /// <summary>A checked-in stay of <paramref name="property"/> departing today, entered by the host.</summary>
    private async Task<Guid> SeedStayAsync(Property property, decimal touristTax = 0m)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = Today.AddDays(-2),
            CheckOutDate = Today,
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.CheckedIn,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = 300m + touristTax,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>An active cleaning supplier working in <paramref name="comune"/>.</summary>
    private async Task<Guid> SeedSupplierAsync(string comune)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Pulizie Roma",
            Slug = $"supplier-{Guid.NewGuid():N}",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            LegalName = "Pulizie Roma Srl",
            Email = $"supplier-{Guid.NewGuid():N}@example.com",
            Phone = "+3906123456",
            Status = SupplierStatus.Active,
            CategoriesJson = """["cleaning"]""",
            ComuniJson = $$"""["{{comune}}"]""",
            TosAcceptedAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<Booking> LoadAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }

    /// <summary>The app with its clock fixed at <see cref="Now"/> (FD-06): "today" never depends on when the tests run.</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        }
    }
}
