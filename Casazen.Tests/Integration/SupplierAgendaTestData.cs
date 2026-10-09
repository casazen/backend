using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>Seeds for the agenda tests (SP-03): rows written straight to the database, valid on PostgreSQL and in memory.</summary>
internal static class SupplierAgendaTestData
{
    /// <summary>A manual window or one of the calendar feed of <paramref name="orgId"/>.</summary>
    public static async Task<SupplierBusyWindow> SeedWindowAsync(
        CasazenWebApplicationFactory factory,
        Guid orgId,
        DateTime startUtc,
        DateTime endUtc,
        SupplierBusyWindowKind kind,
        SupplierBusyWindowSource source = SupplierBusyWindowSource.Manual)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var window = new SupplierBusyWindow
        {
            OrgId = orgId,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Kind = kind,
            Source = source,
            ExternalUid = source == SupplierBusyWindowSource.ICalFeed ? $"uid-{Guid.NewGuid():N}" : null,
            Label = "Riservato al fornitore",
        };
        db.SupplierBusyWindows.Add(window);
        await db.SaveChangesAsync();
        return window;
    }

    /// <summary>A day override (<c>SupplierAvailability</c>).</summary>
    public static async Task SeedDayAsync(
        CasazenWebApplicationFactory factory,
        Guid orgId,
        DateOnly date,
        bool available,
        SupplierAvailabilitySource source = SupplierAvailabilitySource.Manual)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierAvailability.Add(new SupplierAvailability { OrgId = orgId, Date = date, Available = available, Source = source });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A host with a property and a guest, a stay checking out on <paramref name="checkOut"/> and a service request for it sent to
    /// <paramref name="supplierOrgId"/> in <paramref name="status"/>. Returns the request, the address of the property and the
    /// guest's name (what must never reach the supplier's calendar).
    /// </summary>
    public static async Task<(Guid RequestId, string Address, string GuestName)> SeedRequestAsync(
        CasazenWebApplicationFactory factory,
        Guid supplierOrgId,
        DateOnly checkOut,
        ServiceRequestStatus status)
    {
        var ownerId = $"auth0|sp03-host-{Guid.NewGuid():N}";
        var hostOrg = await factory.SeedOrgForOwnerAsync(ownerId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var address = $"Via Segretissima {Guid.NewGuid():N}";
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = hostOrg.Id,
            Name = "Casa Riservata",
            Address = address,
            City = "Roma",
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
            FirstName = "Romeo",
            LastName = "Montecchi",
            Email = $"romeo.montecchi-{Guid.NewGuid():N}@example.com",
            PhoneNumber = "+39 347 0001111",
            Address = "Via dell'Ospite 1",
            City = "Verona",
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentNumber = "AY0000001",
        };
        var stay = new Booking
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = checkOut.AddDays(-3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(property, guest, stay);
        await db.SaveChangesAsync();

        var request = new ServiceRequest
        {
            OrgId = hostOrg.Id,
            BookingId = stay.Id,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            Category = ServiceCategories.Cleaning,
            Status = status,
            Notes = "Note riservate dell'host",
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return (request.Id, address, "Montecchi");
    }

    /// <summary>The number of rows each agenda table holds for <paramref name="orgId"/>.</summary>
    public static async Task<(int Hours, int TimeOff, int Windows, int Settings)> CountsAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.SupplierWorkingHours.Where(h => h.OrgId == orgId).CountAsync(),
            await db.SupplierTimeOff.Where(t => t.OrgId == orgId).CountAsync(),
            await db.SupplierBusyWindows.Where(w => w.OrgId == orgId).CountAsync(),
            await db.SupplierSettings.Where(s => s.OrgId == orgId).CountAsync());
    }
}
