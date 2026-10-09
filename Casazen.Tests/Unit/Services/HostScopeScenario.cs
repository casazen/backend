using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data every «collaborator Solo alcuni sees only its own» test runs on (AM-03): an org with two properties, the
/// collaborator who was given only the first, and a second org whose data nobody here may see. Each property has one of
/// everything the lists of the host are made of (stays, payments with OTA withholding, a lease, an iCal feed, an intervention,
/// a request waiting for the host, a closed stay with its check-out record), so a test can say «only A's» about each list.
/// Shared by the InMemory tests and the PostgreSQL ones: nothing here depends on the provider.
/// </summary>
internal sealed record HostScopeWorld(
    Guid OrgId,
    Guid OtherOrgId,
    Guid SupplierOrgId,
    string OwnerUserId,
    string CollaboratorId,
    Property Granted,
    Property Hidden,
    Property OtherOrgProperty)
{
    /// <summary>The scope of the collaborator «Solo alcuni»: the properties it was given.</summary>
    public HostScope Restricted => new(OrgId, GrantedToUserId: CollaboratorId);

    /// <summary>The scope of the owner, the administrators, the managers: the whole org.</summary>
    public HostScope OrgWide => new(OrgId);

    /// <summary>The scope of an account in no org team that created the hidden property: the old rule.</summary>
    public HostScope OwnedByOwner => new(OrgId, OwnerId: OwnerUserId);
}

internal static class HostScopeScenario
{
    /// <summary>The instant the services run at: 2026-10-09 in Rome.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    public static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    public static AppDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static async Task<HostScopeWorld> SeedAsync(AppDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = NewOrg("Host", $"host-{suffix}");
        var otherOrg = NewOrg("Altro host", $"altro-{suffix}");
        var supplierOrg = NewOrg("Fornitore", $"forn-{suffix}", OrgType.Supplier);
        var owner = NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
        var collaborator = NewUser($"auth0|collaboratore-{suffix}", org.Id, UserRole.None);
        var otherOwner = NewUser($"auth0|altro-{suffix}", otherOrg.Id, UserRole.PropertyOwner);
        db.Orgs.AddRange(org, otherOrg, supplierOrg);
        db.Users.AddRange(owner, collaborator, otherOwner);
        await db.SaveChangesAsync();

        var granted = NewProperty(org.Id, owner.Id, "Trullo");
        var hidden = NewProperty(org.Id, owner.Id, "Casa Bianca");
        var foreign = NewProperty(otherOrg.Id, otherOwner.Id, "Villa Altrove");
        db.Properties.AddRange(granted, hidden, foreign);
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

        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = collaborator.Id, PropertyId = granted.Id });

        foreach (var property in new[] { granted, hidden, foreign })
            AddDataOf(db, property, supplierOrg.Id);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new HostScopeWorld(org.Id, otherOrg.Id, supplierOrg.Id, owner.Id, collaborator.Id, granted, hidden, foreign);
    }

    /// <summary>
    /// Two properties of an org that already exists (the HTTP tests build the org and its people through the real services),
    /// each with everything the lists are made of. Returns them with the supplier org the interventions were requested from.
    /// </summary>
    public static async Task<(Property Granted, Property Hidden, Guid SupplierOrgId)> SeedPropertiesOfAsync(
        AppDbContext db, Guid orgId, string creatorId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplierOrg = NewOrg("Fornitore", $"forn-{suffix}", OrgType.Supplier);
        db.Orgs.Add(supplierOrg);
        var granted = NewProperty(orgId, creatorId, "Trullo");
        var hidden = NewProperty(orgId, creatorId, "Casa Bianca");
        db.Properties.AddRange(granted, hidden);
        await db.SaveChangesAsync();

        foreach (var property in new[] { granted, hidden })
            AddDataOf(db, property, supplierOrg.Id);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (granted, hidden, supplierOrg.Id);
    }

    /// <summary>
    /// The checks the PostgreSQL schema makes and the InMemory provider does not: every foreign key that has a value points at a
    /// row that exists, and no unique index holds the same key twice. It lets the data of the scenario be proved sound without a
    /// database (the PostgreSQL tests of CI seed the same rows and would fail on a dangling key or a duplicate).
    /// </summary>
    public static async Task AssertReferentialIntegrityAsync(AppDbContext db)
    {
        db.ChangeTracker.Clear();
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
        var problems = new List<string>();

        foreach (var entityType in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && !e.HasSharedClrType && e.FindPrimaryKey() is not null))
        {
            var rows = ((System.Collections.IEnumerable)set.MakeGenericMethod(entityType.ClrType).Invoke(db, null)!).Cast<object>().ToList();

            foreach (var foreignKey in entityType.GetForeignKeys().Where(f => f.PrincipalKey.IsPrimaryKey() && !f.PrincipalEntityType.IsOwned()))
            {
                foreach (var row in rows)
                {
                    var entry = db.Entry(row);
                    var values = foreignKey.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
                    if (values.Any(v => v is null))
                        continue;

                    if (await db.FindAsync(foreignKey.PrincipalEntityType.ClrType, values!) is null)
                    {
                        problems.Add($"{entityType.DisplayName()}.{string.Join("+", foreignKey.Properties.Select(p => p.Name))} = "
                                     + $"{string.Join("+", values)} has no {foreignKey.PrincipalEntityType.DisplayName()}");
                    }
                }
            }

            foreach (var index in entityType.GetIndexes().Where(i => i.IsUnique && i.GetFilter() is null))
            {
                var keys = rows
                    .Select(row => index.Properties.Select(p => db.Entry(row).Property(p.Name).CurrentValue).ToArray())
                    .Where(values => values.All(v => v is not null))
                    .Select(values => string.Join("|", values))
                    .GroupBy(key => key)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key);
                problems.AddRange(keys.Select(key =>
                    $"{entityType.DisplayName()} has the key {key} twice in the unique index {string.Join("+", index.Properties.Select(p => p.Name))}"));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Everything the lists are made of, for one property: its name says whose it is in the assertions.</summary>
    internal static void AddDataOf(AppDbContext db, Property property, Guid supplierOrgId)
    {
        var orgId = property.OrgId;
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = property.Name, Email = $"{Guid.NewGuid():N}@example.com" };

        // A confirmed stay that starts tomorrow (Rome): a check-in to complete, an arrival to come, revenue of the month.
        var upcoming = NewBooking(property, guest, BookingStatus.Confirmed, Day(2026, 10, 10), Day(2026, 10, 13));
        // A stay already checked out, with the check-out record of the turnover still open.
        var closed = NewBooking(property, guest, BookingStatus.CheckedOut, Day(2026, 10, 1), Day(2026, 10, 4));
        // A «pay at the property» request waiting for the host.
        var request = NewBooking(property, guest, BookingStatus.Pending, Day(2026, 11, 20), Day(2026, 11, 23));
        request.Source = BookingSource.Direct;
        request.PaymentOption = PaymentOption.OnSite;
        request.GuestEmailVerifiedAt = Now.UtcDateTime;
        request.RequestExpiresAt = Now.UtcDateTime.AddDays(1);

        db.Guests.Add(guest);
        db.Bookings.AddRange(upcoming, closed, request);

        // Money of the stays, with the OTA withholding the withholding report is made of.
        foreach (var booking in new[] { upcoming, closed })
        {
            db.Payments.Add(new Payment
            {
                OrgId = orgId,
                BookingId = booking.Id,
                Amount = 300m,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                TransactionId = $"tx-{Guid.NewGuid():N}",
                OtaWithholdingTax = 63m,
                WithholdingTaxApplied = true,
                NetAmountAfterWithholding = 237m,
                WithholdingSource = WithholdingSource.AutoOta,
                ProcessedAt = Day(2026, 10, 5),
                CreatedAt = Day(2026, 10, 5),
                UpdatedAt = Day(2026, 10, 5),
            });
        }

        db.StayCheckouts.Add(new StayCheckout
        {
            OrgId = orgId,
            BookingId = closed.Id,
            CompletedAt = Day(2026, 10, 4),
            PropertyReadyAt = null,
            CurrentStep = CheckoutWizardStep.PropertyReady,
        });
        db.LeaseContracts.Add(new LeaseContract
        {
            OrgId = orgId,
            PropertyId = property.Id,
            Status = LeaseStatus.Draft,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = Day(2027, 1, 1),
            EndDate = Day(2030, 12, 31),
            MonthlyRent = 800m,
        });
        db.PropertyICalFeeds.Add(new PropertyICalFeed
        {
            OrgId = orgId,
            PropertyId = property.Id,
            Channel = ICalFeedChannel.Airbnb,
            Label = $"Airbnb {property.Name}",
        });
        db.ServiceRequests.Add(new ServiceRequest
        {
            OrgId = orgId,
            PropertyId = property.Id,
            BookingId = upcoming.Id,
            SupplierOrgId = supplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = "cleaning",
            Status = ServiceRequestStatus.Richiesto,
        });
    }

    internal static OrgEntity NewOrg(string name, string slug, OrgType type = OrgType.Host) => new()
    {
        Name = name,
        Slug = slug,
        DisplayName = name,
        ContactEmail = $"{slug}@example.com",
        OrgType = type,
    };

    internal static User NewUser(string id, Guid orgId, UserRole role) => new()
    {
        Id = id,
        Email = $"{Guid.NewGuid():N}@example.com",
        FirstName = "Prova",
        LastName = "Utente",
        OrgId = orgId,
        Role = role,
        IsActive = true,
    };

    internal static Property NewProperty(Guid orgId, string ownerId, string name) => new()
    {
        OrgId = orgId,
        OwnerId = ownerId,
        Name = name,
        Address = $"Via {name} {Guid.NewGuid():N}",
        City = "Ostuni",
        PostalCode = "72017",
        Bedrooms = 2,
        Bathrooms = 1,
        MaxGuests = 4,
        NightlyRate = 100m,
        IsActive = true,
    };

    private static Booking NewBooking(Property property, Guest guest, BookingStatus status, DateTime checkIn, DateTime checkOut) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        GuestId = guest.Id,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        Status = status,
        Source = BookingSource.Manual,
        BasePrice = 300m,
        TotalPrice = 300m,
        TouristTaxAmount = 6m,
        TouristTax = 6m,
    };
}
