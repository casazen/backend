using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03: the emails to the host side of a new booking go to the contact address of the org and to the people who must hear
/// about the property: the member in charge of it and the administrators of the org (the creator while nobody is named). The
/// same rule as the push (<c>PushDeliveryJobTests</c>), one message each, no address twice, nobody who is deactivated.
/// </summary>
public class BookingNotifierHostAudienceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"booking-audience-{Guid.NewGuid():N}")
        .Options);

    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();

    public void Dispose() => _db.Dispose();

    private sealed record World(Guid OrgId, Guid PropertyId, Guid BookingId);

    private BookingNotifier Notifier() =>
        new(_db, _emails, EmailTestHelpers.Links(), _pushes, NullLogger<BookingNotifier>.Instance);

    private User AddPerson(Guid orgId, string id, OrgRole? role, OrgMemberStatus status = OrgMemberStatus.Active, UserRole userRole = UserRole.None, bool active = true)
    {
        var user = new User
        {
            Id = $"auth0|{id}",
            Email = $"{id}@example.com",
            FirstName = id,
            LastName = "Test",
            OrgId = orgId,
            Role = userRole,
            IsActive = active,
        };
        _db.Users.Add(user);
        if (role is not null)
            _db.OrgMembers.Add(new OrgMember { UserId = user.Id, OrgId = orgId, Role = role.Value, Status = status });
        return user;
    }

    /// <summary>An org with its contact address, an owner who created the property, and a confirmed booking on it.</summary>
    private async Task<World> SeedAsync(string? responsibleId = null, string creatorId = "owner")
    {
        var org = new OrgEntity { Name = "Villa Org", Slug = "villa-org", DisplayName = "Villa Org", ContactEmail = "contatto@example.com" };
        _db.Orgs.Add(org);
        AddPerson(org.Id, "owner", OrgRole.Owner);
        AddPerson(org.Id, "manager", OrgRole.PropertyManager);
        AddPerson(org.Id, "admin", OrgRole.Admin);
        AddPerson(org.Id, "fired-admin", OrgRole.Admin, OrgMemberStatus.Deactivated);
        AddPerson(org.Id, "collaborator", OrgRole.Collaborator);
        AddPerson(org.Id, "accountant", OrgRole.Accountant);
        AddPerson(org.Id, "platform-admin", role: null, userRole: UserRole.Admin);
        AddPerson(org.Id, "locked-admin", OrgRole.Admin, active: false);

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = $"auth0|{creatorId}",
            ResponsibleUserId = responsibleId is null ? null : $"auth0|{responsibleId}",
            Name = "Villa Rosa",
            Address = "Via Roma 1",
            City = "Roma",
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            TotalPrice = 350m,
        };
        _db.AddRange(property, guest, booking);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new World(org.Id, property.Id, booking.Id);
    }

    private List<string?> HostRecipients() => _emails.Snapshot()
        .Where(e => e.Template == EmailTemplates.Names.HostBookingConfirmed)
        .Select(e => e.To)
        .OrderBy(to => to, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public async Task BookingConfirmedAsync_TheContactTheMemberInChargeAndTheAdministratorsAreEmailed_OneMessageEach()
    {
        var world = await SeedAsync(responsibleId: "manager");

        await Notifier().BookingConfirmedAsync(world.BookingId, BookingConfirmationKind.PaidOnline);

        // The org contact, the manager in charge, the owner and the admin: not the collaborator, the accountant, the platform
        // admin, nor the deactivated or locked administrators.
        Assert.Equal(
            ["admin@example.com", "contatto@example.com", "manager@example.com", "owner@example.com"],
            HostRecipients());
        // The guest has its own message.
        Assert.Single(_emails.Snapshot(), e => e.To == "guest@example.com");
    }

    [Fact]
    public async Task BookingConfirmedAsync_NobodyInCharge_TheCreatorStandsIn()
    {
        var world = await SeedAsync(responsibleId: null, creatorId: "manager");

        await Notifier().BookingConfirmedAsync(world.BookingId, BookingConfirmationKind.PaidOnline);

        Assert.Equal(
            ["admin@example.com", "contatto@example.com", "manager@example.com", "owner@example.com"],
            HostRecipients());
    }

    [Fact]
    public async Task BookingConfirmedAsync_TheContactAddressOfAnAdministrator_IsEmailedOnce()
    {
        var world = await SeedAsync(responsibleId: "manager");
        var org = await _db.Orgs.SingleAsync(o => o.Id == world.OrgId);
        org.ContactEmail = "Owner@Example.com";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Notifier().BookingConfirmedAsync(world.BookingId, BookingConfirmationKind.PaidOnline);

        Assert.Equal(["Owner@Example.com", "admin@example.com", "manager@example.com"], HostRecipients().Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task BookingConfirmedAsync_TheMemberInChargeWasDeactivated_OnlyTheContactAndTheAdministratorsHear()
    {
        var world = await SeedAsync(responsibleId: "manager");
        var manager = await _db.OrgMembers.SingleAsync(m => m.UserId == "auth0|manager");
        manager.Status = OrgMemberStatus.Deactivated;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Notifier().BookingConfirmedAsync(world.BookingId, BookingConfirmationKind.PaidOnline);

        Assert.Equal(["admin@example.com", "contatto@example.com", "owner@example.com"], HostRecipients());
    }
}
