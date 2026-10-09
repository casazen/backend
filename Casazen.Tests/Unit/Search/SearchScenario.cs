using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// The data every global search test runs on (UI-13a): an org with properties, guests, stays, leases, interventions and suppliers
/// whose names have accents and look alike, a collaborator who was given only the first property, and a second org whose data
/// carries the same words and must never be found. Shared by the in-memory tests, the HTTP ones and the PostgreSQL ones: nothing here
/// depends on the provider. The rows are written through the model; on in-memory the keys are then written by
/// <see cref="SearchKeysForTests"/>, on PostgreSQL the database computes them.
/// </summary>
internal sealed record SearchWorld(
    Guid OrgId,
    Guid OtherOrgId,
    string OwnerId,
    string CollaboratorId,
    Property Trullo,
    Property CasaBella,
    Property LoftNavigli,
    Property Deleted,
    Property Switched,
    Property Foreign,
    Guest Jose,
    Guest MariaRossi,
    Guest MarioRossi,
    Guest Anonymized,
    Guest WithoutStay,
    Guest DeletedGuest,
    Guest ForeignMaria,
    Booking StayJose,
    Booking StayMaria,
    Booking StayMarioAtTrullo,
    Booking StayMarioAtCasaBella,
    Booking StayAnonymized,
    Booking StayForeign,
    LeaseContract LeaseVerdi,
    LeaseContract LeaseNeri,
    LeaseContract LeaseForeign,
    ServiceRequest Cleaning,
    ServiceRequest Plumbing,
    ServiceRequest Boiler,
    ServiceRequest ForeignRequest,
    ServiceRequest SuspendedRequest,
    Guid CleanersOrgId,
    Guid PlumbersOrgId,
    Guid SuspendedOrgId,
    Guid IdleOrgId)
{
    /// <summary>The scope of the collaborator «Solo alcuni»: the first property only.</summary>
    public HostScope Collaborator => new(OrgId, GrantedToUserId: CollaboratorId);

    /// <summary>The owner, the administrators, the managers: the whole org.</summary>
    public HostScope OrgWide => new(OrgId);

    /// <summary>An account in no team that created every property of the org: the old rule.</summary>
    public HostScope OwnedByOwner => new(OrgId, OwnerId: OwnerId);

    /// <summary>The scope of the second org.</summary>
    public HostScope OtherOrg => new(OtherOrgId);

    /// <summary>What an owner who holds every permission may search.</summary>
    public Casazen.Core.Search.HostSearchAccess Everything(HostScope? scope = null) =>
        new(scope ?? OrgWide, true, true, true, true, true);
}

internal static class SearchScenario
{
    public static AppDbContext NewInMemoryDb() => HostScopeScenario.NewInMemoryDb();

    /// <summary>
    /// Writes the world with its own org, owner and collaborator. <paramref name="computeKeys"/> writes the search keys too
    /// (in-memory; PostgreSQL computes them).
    /// </summary>
    public static async Task<SearchWorld> SeedAsync(AppDbContext db, bool computeKeys)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
        var collaborator = HostScopeScenario.NewUser($"auth0|collaboratore-{suffix}", org.Id, UserRole.None);
        db.Orgs.Add(org);
        db.Users.AddRange(owner, collaborator);
        await db.SaveChangesAsync();

        db.OrgMembers.AddRange(
            new OrgMember { OrgId = org.Id, UserId = owner.Id, Role = OrgRole.Owner },
            new OrgMember
            {
                OrgId = org.Id,
                UserId = collaborator.Id,
                Role = OrgRole.Collaborator,
                PropertyScope = PropertyScope.Selected,
            });
        await db.SaveChangesAsync();

        return await SeedDataAsync(db, org.Id, owner.Id, collaborator.Id, computeKeys);
    }

    /// <summary>
    /// Writes the data of the world into an org that already exists with its owner and its collaborator (the HTTP tests build them
    /// through the real services): the properties, the guests, the stays, the leases, the interventions, the suppliers, the other org,
    /// and the grant of the first property to the collaborator. The caller sets the scope of the collaborator member to «Solo alcuni».
    /// </summary>
    public static async Task<SearchWorld> SeedDataAsync(
        AppDbContext db, Guid orgId, string ownerId, string collaboratorId, bool computeKeys)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var otherOrg = HostScopeScenario.NewOrg("Altro host", $"altro-{suffix}");
        var cleaners = HostScopeScenario.NewOrg("Pulizie Splendore", $"pulizie-{suffix}", OrgType.Supplier);
        var plumbers = HostScopeScenario.NewOrg("Idraulica Rossi", $"idraulica-{suffix}", OrgType.Supplier);
        var suspended = HostScopeScenario.NewOrg("Fornitore sospeso", $"sospeso-{suffix}", OrgType.Supplier);
        var idle = HostScopeScenario.NewOrg("Giardini Verdi", $"giardini-{suffix}", OrgType.Supplier);
        var otherOwner = HostScopeScenario.NewUser($"auth0|altro-{suffix}", otherOrg.Id, UserRole.PropertyOwner);
        db.Orgs.AddRange(otherOrg, cleaners, plumbers, suspended, idle);
        db.Users.Add(otherOwner);
        await db.SaveChangesAsync();

        // The suppliers: three active, one suspended. «Idle» was never asked anything by the host.
        db.SupplierProfiles.AddRange(
            Supplier(cleaners.Id, "Pulizie Splendore S.r.l.", "[\"cleaning\",\"laundry\"]", SupplierStatus.Active, $"pulizie-{suffix}"),
            Supplier(plumbers.Id, "Idraulica Rossi S.r.l.", "[\"plumbing\"]", SupplierStatus.Active, $"idraulica-{suffix}"),
            Supplier(suspended.Id, "Idraulica Sospesa", "[\"plumbing\"]", SupplierStatus.Suspended, $"sospeso-{suffix}"),
            Supplier(idle.Id, "Giardini Verdi", "[\"gardening\"]", SupplierStatus.Active, $"giardini-{suffix}"));

        var trullo = Property(orgId, ownerId, "Trullo Bianco", "Alberobello", "IT072003C2ABCD12", RentalMode.Short);
        var casaBella = Property(orgId, ownerId, "Casa Bella", "Forlì", null, RentalMode.Short);
        var loft = Property(orgId, ownerId, "Loft Navigli", "Milano", null, RentalMode.Long);
        var deleted = Property(orgId, ownerId, "Villa Cancellata", "Roma", null, RentalMode.Short);
        deleted.IsDeleted = true;
        deleted.DeletedAt = HostScopeScenario.Now.UtcDateTime;
        var switchedOff = Property(orgId, ownerId, "Vecchio Rustico", "Perugia", null, RentalMode.Short);
        switchedOff.IsActive = false;
        var foreign = Property(otherOrg.Id, otherOwner.Id, "Trullo Altrove", "Forlì", null, RentalMode.Short);
        db.Properties.AddRange(trullo, casaBella, loft, deleted, switchedOff, foreign);
        await db.SaveChangesAsync();
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = collaboratorId, PropertyId = trullo.Id });

        // The guests. Jose has accents; the two Rossi share a surname; one guest was anonymized, one deleted, one has no stay.
        var jose = new Guest
        {
            OrgId = orgId,
            FirstName = "José",
            LastName = "Müller",
            Email = "jose.muller@example.com",
            PhoneNumber = "+391234567890",
            DocumentNumber = "AX1234567",
            City = "Monaco",
        };
        var maria = new Guest
        {
            OrgId = orgId,
            FirstName = "Maria",
            LastName = "Rossi",
            Email = "maria.rossi@example.com",
            PhoneNumber = "+390612345678",
            DocumentNumber = "CA00000AA",
        };
        var mario = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", PhoneNumber = "+39333111222" };
        var anonymous = new Guest
        {
            OrgId = orgId,
            FirstName = "ANONYMIZED",
            LastName = "ANONYMIZED",
            Email = "ANON-1@deleted.local",
            DataAnonymizedDate = HostScopeScenario.Now.UtcDateTime,
        };
        var noStay = new Guest { OrgId = orgId, FirstName = "Carla", LastName = "Senzasoggiorno", Email = "carla@example.com" };
        var erased = new Guest { OrgId = orgId, FirstName = "Elena", LastName = "Cancellata", Email = "elena@example.com", IsDeleted = true };
        var foreignMaria = new Guest { OrgId = otherOrg.Id, FirstName = "Maria", LastName = "Rossi", Email = "maria@altrove.example.com" };
        db.Guests.AddRange(jose, maria, mario, anonymous, noStay, erased, foreignMaria);
        await db.SaveChangesAsync();

        // Stays. The codes are in the alphabet of BookingCodes (no I, L, O, U).
        var stayJose = Stay(trullo, jose, "7K3M9PQ2XV", 2026, 10, 10);
        var stayMaria = Stay(casaBella, maria, "A1B2C3D4E5", 2026, 10, 12);
        var stayMarioTrullo = Stay(trullo, mario, "ZXCV12345K", 2026, 10, 14);
        var stayMarioBella = Stay(casaBella, mario, "MNBVC98765", 2026, 10, 20);
        var stayAnonymous = Stay(trullo, anonymous, "QWERT12345", 2026, 10, 2);
        var stayForeign = Stay(foreign, foreignMaria, "XYZ9876543", 2026, 10, 11);
        db.Bookings.AddRange(stayJose, stayMaria, stayMarioTrullo, stayMarioBella, stayAnonymous, stayForeign);
        await db.SaveChangesAsync();

        // Leases: the Verdi (two tenants) at the long-term property, one tenant at the trullo; the landlord is a party too.
        var leaseVerdi = Lease(orgId, loft.Id, 1);
        var leaseNeri = Lease(orgId, trullo.Id, 2);
        var leaseForeign = Lease(otherOrg.Id, foreign.Id, 3);
        db.LeaseContracts.AddRange(leaseVerdi, leaseNeri, leaseForeign);
        await db.SaveChangesAsync();
        db.Parties.AddRange(
            Party(leaseVerdi, PartyRole.Landlord, 0, "Titolare", "Proprietario"),
            Party(leaseVerdi, PartyRole.Tenant, 0, "Giulia", "Verdi"),
            Party(leaseVerdi, PartyRole.Tenant, 1, "Paolo", "Verdi"),
            Party(leaseVerdi, PartyRole.Tenant, 2, "Chiara", "Bianchi", anonymizedAt: HostScopeScenario.Now.UtcDateTime),
            Party(leaseNeri, PartyRole.Landlord, 0, "Titolare", "Proprietario"),
            Party(leaseNeri, PartyRole.Tenant, 0, "Luca", "Neri"),
            Party(leaseForeign, PartyRole.Tenant, 0, "Giulia", "Verdi"));

        // Interventions asked of the suppliers by the host (short stay, long term) and by the other org.
        var cleaning = Request(orgId, trullo.Id, stayJose.Id, cleaners.Id, ServiceRequestRentalContext.ShortRent, "cleaning", "Pulizia finale");
        var plumbing = Request(orgId, casaBella.Id, stayMaria.Id, plumbers.Id, ServiceRequestRentalContext.ShortRent, "plumbing", null);
        var boiler = Request(orgId, loft.Id, null, plumbers.Id, ServiceRequestRentalContext.LongRent, "maintenance", "Riparazione caldaia");
        var foreignRequest = Request(otherOrg.Id, foreign.Id, stayForeign.Id, cleaners.Id, ServiceRequestRentalContext.ShortRent, "cleaning", "Pulizia finale");
        // The suspended supplier was asked something too, but it is not active.
        var suspendedRequest = Request(orgId, casaBella.Id, stayMaria.Id, suspended.Id, ServiceRequestRentalContext.ShortRent, "plumbing", "Sblocco scarico");
        db.ServiceRequests.AddRange(cleaning, plumbing, boiler, foreignRequest, suspendedRequest);
        await db.SaveChangesAsync();

        if (computeKeys)
            await SearchKeysForTests.ComputeAsync(db);
        db.ChangeTracker.Clear();

        return new SearchWorld(
            orgId, otherOrg.Id, ownerId, collaboratorId,
            trullo, casaBella, loft, deleted, switchedOff, foreign,
            jose, maria, mario, anonymous, noStay, erased, foreignMaria,
            stayJose, stayMaria, stayMarioTrullo, stayMarioBella, stayAnonymous, stayForeign,
            leaseVerdi, leaseNeri, leaseForeign,
            cleaning, plumbing, boiler, foreignRequest, suspendedRequest,
            cleaners.Id, plumbers.Id, suspended.Id, idle.Id);
    }

    private static SupplierProfile Supplier(Guid orgId, string legalName, string categoriesJson, SupplierStatus status, string slug) => new()
    {
        OrgId = orgId,
        LegalName = legalName,
        Phone = "+39 06 0000000",
        Email = $"{slug}@fornitori.example.com",
        CategoriesJson = categoriesJson,
        Status = status,
        ShowcaseSlug = slug,
    };

    private static Property Property(Guid orgId, string ownerId, string name, string city, string? cin, RentalMode mode)
    {
        var property = HostScopeScenario.NewProperty(orgId, ownerId, name);
        property.City = city;
        property.CinCode = cin;
        property.RentalMode = mode;
        return property;
    }

    private static Booking Stay(Property property, Guest guest, string code, int year, int month, int day) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        GuestId = guest.Id,
        BookingCode = code,
        CheckInDate = HostScopeScenario.Day(year, month, day),
        CheckOutDate = HostScopeScenario.Day(year, month, day + 2),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Manual,
        BasePrice = 300m,
        TotalPrice = 300m,
    };

    private static LeaseContract Lease(Guid orgId, Guid propertyId, int month) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        Status = LeaseStatus.Draft,
        FiscalRegime = FiscalRegime.CedolareSecca,
        StartDate = HostScopeScenario.Day(2027, month, 1),
        EndDate = HostScopeScenario.Day(2031, month, 1),
        MonthlyRent = 800m,
        CreatedAt = HostScopeScenario.Day(2026, 9, month),
    };

    private static Party Party(LeaseContract lease, PartyRole role, int position, string firstName, string lastName, DateTime? anonymizedAt = null) => new()
    {
        LeaseContractId = lease.Id,
        Role = role,
        Position = position,
        FirstName = firstName,
        LastName = lastName,
        FiscalCode = "RSSMRA80A01H501Z",
        Citizenship = "IT",
        ContactEmail = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.com",
        AnonymizedAt = anonymizedAt,
    };

    private static ServiceRequest Request(
        Guid orgId, Guid propertyId, Guid? bookingId, Guid supplierOrgId, ServiceRequestRentalContext context, string category, string? serviceName) => new()
        {
            OrgId = orgId,
            PropertyId = propertyId,
            BookingId = bookingId,
            SupplierOrgId = supplierOrgId,
            RentalContext = context,
            Category = category,
            ServiceNameSnapshot = serviceName,
            Status = ServiceRequestStatus.Richiesto,
            CreatedAt = HostScopeScenario.Now.UtcDateTime,
        };
}
