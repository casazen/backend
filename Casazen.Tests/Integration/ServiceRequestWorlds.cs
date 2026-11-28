using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

internal sealed record ServiceRequestWorld(
    string HostUserId,
    Guid HostOrgId,
    Guid PropertyId,
    Guid BookingId,
    string SupplierUserId,
    Guid SupplierOrgId,
    Guid ListingId);

/// <summary>
/// Seeds for the HTTP tests of the service request flows (SP-04). Every world is a new host and a new supplier, so tests never
/// share requests, slots or inbox rows even on one database. Rows go straight to the tables, valid on PostgreSQL and in memory.
/// </summary>
internal static class ServiceRequestWorlds
{
    public const string Comune = "H501";
    public const string ServiceName = "Pulizia appartamento";
    public const string PropertyName = "Casa SP-04 Riservata";
    public const string PropertyAddress = "Via Riservata";
    public const string HostNotes = "Citofono rotto, telefonare";

    public static async Task<ServiceRequestWorld> SeedAsync(
        CasazenWebApplicationFactory factory,
        bool withHours = true,
        string propertyCity = Comune,
        string? propertyName = null)
    {
        var hostUserId = $"auth0|sp04-host-{Guid.NewGuid():N}";
        var hostOrg = await factory.SeedOrgForOwnerAsync(hostUserId);
        var (supplierUserId, supplierOrgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = hostUserId,
            OrgId = hostOrg.Id,
            Name = propertyName ?? PropertyName,
            Address = $"{PropertyAddress} {Guid.NewGuid():N}",
            City = propertyCity,
            PostalCode = "00184",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = hostOrg.Id,
            FirstName = "Giulia",
            LastName = "Ospite",
            Email = $"sp04-{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(7),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(10),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        var listing = SupplierCatalogTestData.Row(supplierOrgId, $"pulizia-{Guid.NewGuid():N}"[..24]);
        listing.Name = ServiceName;
        listing.PriceFromCents = 6000;
        listing.DurationMinutes = 120;
        db.AddRange(property, guest, booking, listing);

        if (withHours)
        {
            foreach (var weekday in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = supplierOrgId, Weekday = weekday, StartMinute = 8 * 60, EndMinute = 13 * 60 });
                db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = supplierOrgId, Weekday = weekday, StartMinute = 14 * 60, EndMinute = 18 * 60 });
            }

            db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = supplierOrgId, Weekday = DayOfWeek.Saturday, StartMinute = 8 * 60, EndMinute = 14 * 60 });
            db.SupplierSettings.Add(new SupplierSettings
            {
                OrgId = supplierOrgId,
                MinNoticeHours = 0,
                BufferMinutes = 30,
                MaxJobsPerDay = 3,
                HorizonDays = 35,
                SlotStepMinutes = 60,
                HoursConfiguredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return new ServiceRequestWorld(hostUserId, hostOrg.Id, property.Id, booking.Id, supplierUserId, supplierOrgId, listing.Id);
    }

    public static DateTime Slot(int hour = 10, DayOfWeek day = DayOfWeek.Wednesday, int minDaysAhead = 3)
    {
        var date = TimeProvider.System.TodayInRomeAsDateOnly().AddDays(minDaysAhead);
        while (date.DayOfWeek != day)
            date = date.AddDays(1);

        return TimeZoneInfo.ConvertTimeToUtc(
            date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified), RomeCalendar.TimeZone);
    }

    public static HttpClient Host(this CasazenWebApplicationFactory factory, ServiceRequestWorld world) =>
        factory.CreateAuthenticatedClient(world.HostUserId, "PropertyOwner");

    public static HttpClient Supplier(this CasazenWebApplicationFactory factory, ServiceRequestWorld world) =>
        factory.CreateAuthenticatedClient(world.SupplierUserId, "Supplier");

    public static async Task<Guid> CreateRequestAsync(
        this CasazenWebApplicationFactory factory,
        ServiceRequestWorld world,
        DateTime? scheduledStartUtc = null,
        bool withService = true,
        string? notes = HostNotes)
    {
        using var host = factory.Host(world);
        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            notes,
            serviceListingId = withService ? world.ListingId : (Guid?)null,
            scheduledStartUtc,
        });
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
            throw new InvalidOperationException($"Creating the request failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static async Task<ServiceRequest> LoadRequestAsync(this CasazenWebApplicationFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    public static async Task ChangeRequestAsync(this CasazenWebApplicationFactory factory, Guid id, Action<ServiceRequest> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
        change(request);
        await db.SaveChangesAsync();
    }
}
