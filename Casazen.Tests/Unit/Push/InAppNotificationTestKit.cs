using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// UI-12a: a small world on the in-memory database for the tests of the in-app notifications, the same people as the push job
/// tests (<c>PushDeliveryJobTests</c>) but <b>with no device at all</b>: the bell does not depend on a phone.
/// </summary>
/// <remarks>
/// The host org has an owner (<see cref="World.OwnerId"/>, an Owner member), a property manager in charge of the property
/// (<see cref="World.ManagerId"/>), and a user of the org who is neither in charge nor an administrator
/// (<see cref="World.BystanderId"/>). The supplier org has a member (<see cref="World.SupplierMemberId"/>, linked by
/// <c>SupplierOrgId</c> and by <c>OrgId</c>), a person who is a host of another org and the supplier too
/// (<see cref="World.SupplierHostTooId"/>), and an inactive member (<see cref="World.SupplierInactiveId"/>).
/// </remarks>
internal sealed class InAppNotificationTestKit : IDisposable
{
    public static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public InAppNotificationTestKit(bool flagOn = true)
    {
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        Clock = new FakeTimeProvider(new DateTimeOffset(Now));
        Flags = new Mock<IFeatureFlags>();
        Flags.Setup(f => f.IsEnabled(FeatureFlags.InAppNotifications)).Returns(() => FlagOn);
        FlagOn = flagOn;
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public Mock<IFeatureFlags> Flags { get; }

    /// <summary>The state of <c>Features:InAppNotifications</c> seen by the job; a test flips it between the event and the run.</summary>
    public bool FlagOn { get; set; }

    public void Dispose() => Db.Dispose();

    public InAppNotificationJob Job() =>
        new(Db, Flags.Object, Clock, NullLogger<InAppNotificationJob>.Instance);

    public async Task<World> SeedWorldAsync()
    {
        var org = new OrgEntity { Name = "Host Org", Slug = $"host-{Guid.NewGuid():N}"[..20] };
        var otherHostOrg = new OrgEntity { Name = "Other Host", Slug = $"oth-{Guid.NewGuid():N}"[..20] };
        var supplierOrg = new OrgEntity { Name = "Supplier Org", Slug = $"sup-{Guid.NewGuid():N}"[..20], OrgType = OrgType.Supplier };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = World.OwnerId,
            // AM-03: the manager is in charge of this property; the owner (an administrator of the org) is told as well.
            ResponsibleUserId = World.ManagerId,
            Name = "Villa",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Guest", LastName = "One", Email = "guest@example.com" };
        var booking = new Booking
        {
            OrgId = org.Id,
            Property = property,
            Guest = guest,
            CheckInDate = Now.Date.AddDays(1),
            CheckOutDate = Now.Date.AddDays(3),
            Status = BookingStatus.Confirmed,
        };

        Db.Orgs.AddRange(org, otherHostOrg, supplierOrg);
        Db.Properties.Add(property);
        Db.Guests.Add(guest);
        Db.Bookings.Add(booking);
        Db.Users.AddRange(
            NewUser(World.OwnerId, org.Id, UserRole.PropertyOwner),
            NewUser(World.BystanderId, org.Id, UserRole.PropertyOwner),
            NewUser(World.ManagerId, org.Id, UserRole.PropertyManager),
            NewUser(World.SupplierMemberId, supplierOrg.Id, UserRole.Supplier, supplierOrgId: supplierOrg.Id),
            NewUser(World.SupplierHostTooId, otherHostOrg.Id, UserRole.PropertyOwner, supplierOrgId: supplierOrg.Id),
            NewUser(World.SupplierInactiveId, supplierOrg.Id, UserRole.Supplier, supplierOrgId: supplierOrg.Id, active: false));
        Db.OrgMembers.AddRange(
            NewMember(World.OwnerId, org.Id, OrgRole.Owner),
            NewMember(World.ManagerId, org.Id, OrgRole.PropertyManager));

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return new World(org.Id, otherHostOrg.Id, supplierOrg.Id, property.Id, booking.Id);
    }

    public static QueuedInAppNotification Queued(
        PushAudience audience,
        string type = PushTypes.NewBooking,
        Guid? entityId = null,
        DateTime? occurredAt = null) => new()
        {
            AudienceKind = audience.Kind,
            AudienceId = audience.Id,
            Type = type,
            EntityId = entityId,
            OccurredAt = occurredAt ?? Now,
        };

    public static User NewUser(string id, Guid? orgId, UserRole role, Guid? supplierOrgId = null, bool active = true) => new()
    {
        Id = id,
        Email = $"{id.Replace("|", "-")}@example.com",
        OrgId = orgId,
        SupplierOrgId = supplierOrgId,
        Role = role,
        IsActive = active,
    };

    public static OrgMember NewMember(string userId, Guid orgId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active) => new()
    {
        UserId = userId,
        OrgId = orgId,
        Role = role,
        Status = status,
    };

    public static InAppNotification Row(
        string userId,
        Guid orgId,
        DateTime createdAt,
        string type = PushTypes.NewBooking,
        DateTime? readAt = null,
        string? deliveryKey = null) => new()
        {
            UserId = userId,
            OrgId = orgId,
            Type = type,
            EntityId = Guid.NewGuid(),
            DeliveryKey = deliveryKey ?? $"key:{Guid.NewGuid():N}",
            CreatedAt = createdAt,
            ReadAt = readAt,
        };

    public async Task<List<InAppNotification>> RowsAsync(string? userId = null) =>
        await Db.InAppNotifications.AsNoTracking()
            .Where(n => userId == null || n.UserId == userId)
            .OrderBy(n => n.UserId).ThenBy(n => n.CreatedAt)
            .ToListAsync();

    public sealed record World(Guid OrgId, Guid OtherHostOrgId, Guid SupplierOrgId, Guid PropertyId, Guid BookingId)
    {
        public const string OwnerId = "auth0|owner-a";
        public const string ManagerId = "auth0|manager";
        public const string BystanderId = "auth0|bystander";
        public const string SupplierMemberId = "auth0|supplier-member";
        public const string SupplierHostTooId = "auth0|supplier-host-too";
        public const string SupplierInactiveId = "auth0|supplier-inactive";
    }
}
