using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03b: the person in charge of a property is told about it only while they still reach it. A notification carries the name and
/// email of the guest, the dates and the prices of a stay; the review of AM-03 found that a collaborator who had been put in charge
/// of a property and then lost the access to it was still told. The audience (<c>HostNotificationAudience</c>) reads the reach again
/// with the rule that lets a person be put in charge (an active member of the org who is not a collaborator "Solo alcuni", or who was
/// given this property), and fails closed: a stale name, written on another instance or by hand, tells nobody but the
/// administrators. The same rule is proved on the query, on the emails of <c>BookingNotifier</c> and on the push of
/// <c>PushDeliveryJob</c>; the first level (the name goes when the access goes) is in <c>PropertyResponsibilityReleaseTests</c>.
/// </summary>
public class HostNotificationAudienceReachTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();
    private readonly FakeExpoPushClient _expo = new();

    private Guid _orgId;
    private Guid _propertyId;
    private Guid _otherPropertyId;
    private Guid _bookingId;

    public void Dispose() => _db.Dispose();

    private static string Id(string name) => $"auth0|{name}";

    /// <summary>One person of the org: a user and, for a member of the team, its row.</summary>
    private void AddPerson(
        string name,
        OrgRole? role,
        PropertyScope scope = PropertyScope.All,
        OrgMemberStatus status = OrgMemberStatus.Active,
        Guid? memberOfOrg = null)
    {
        _db.Users.Add(new User
        {
            Id = Id(name),
            Email = $"{name}@example.com",
            FirstName = name,
            LastName = "Test",
            OrgId = _orgId,
            Role = UserRole.None,
            IsActive = true,
        });
        if (role is not null)
        {
            _db.OrgMembers.Add(new OrgMember
            {
                UserId = Id(name),
                OrgId = memberOfOrg ?? _orgId,
                Role = role.Value,
                Status = status,
                PropertyScope = scope,
            });
        }

        _db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = Id(name),
            OrgId = _orgId,
            Platform = "android",
            PushToken = $"ExponentPushToken[{name}]",
            DeviceId = Guid.NewGuid().ToString(),
        });
    }

    private void Grant(string name, Guid propertyId) =>
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = _orgId, UserId = Id(name), PropertyId = propertyId });

    /// <summary>
    /// An org with its contact address, two properties and a confirmed stay on the first. The people: the owner and an
    /// administrator (always told), a manager, an accountant, three collaborators (every property, none given, the first given),
    /// one given only the other property, and a legacy account that is in no team.
    /// </summary>
    private async Task SeedAsync()
    {
        var org = new OrgEntity { Name = "Villa Org", Slug = $"villa-{Guid.NewGuid():N}"[..20], DisplayName = "Villa Org", ContactEmail = "contatto@example.com" };
        _db.Orgs.Add(org);
        _orgId = org.Id;
        AddPerson("owner", OrgRole.Owner);
        AddPerson("admin", OrgRole.Admin);
        AddPerson("manager", OrgRole.PropertyManager);
        AddPerson("accountant", OrgRole.Accountant);
        AddPerson("collab-all", OrgRole.Collaborator);
        AddPerson("collab-none", OrgRole.Collaborator, PropertyScope.Selected);
        AddPerson("collab-granted", OrgRole.Collaborator, PropertyScope.Selected);
        AddPerson("collab-other", OrgRole.Collaborator, PropertyScope.Selected);
        AddPerson("legacy", role: null);

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = Id("owner"),
            Name = "Villa Rosa",
            Address = "Via Roma 1",
            City = "Roma",
        };
        var other = new Property { OrgId = org.Id, OwnerId = Id("owner"), Name = "Casa Blu", Address = "Via Milano 2", City = "Milano" };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            TotalPrice = 350m,
        };
        _db.AddRange(property, other, guest, booking);
        await _db.SaveChangesAsync();

        Grant("collab-granted", property.Id);
        Grant("collab-other", other.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        (_propertyId, _otherPropertyId, _bookingId) = (property.Id, other.Id, booking.Id);
    }

    private async Task NameAsync(string? responsible, string creator = "owner")
    {
        var property = await _db.Properties.SingleAsync(p => p.Id == _propertyId);
        property.ResponsibleUserId = responsible is null ? null : Id(responsible);
        property.OwnerId = Id(creator);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<string[]> ToldAsync(string? responsible, string creator = "owner")
    {
        var told = await HostNotificationAudience
            .UsersToTell(_db, _orgId, _propertyId, responsible is null ? null : Id(responsible), Id(creator))
            .Select(u => u.Id.Replace("auth0|", string.Empty))
            .ToListAsync();
        return told.Order(StringComparer.Ordinal).ToArray();
    }

    // --- The audience -----------------------------------------------------------------------------------

    [Fact]
    public async Task UsersToTell_ACollaboratorWithEveryProperty_InCharge_IsTold()
    {
        await SeedAsync();

        Assert.Equal(["admin", "collab-all", "owner"], await ToldAsync("collab-all"));
    }

    [Fact]
    public async Task UsersToTell_ACollaboratorSoloAlcuniWhoWasNotGivenTheProperty_InCharge_IsNotTold()
    {
        await SeedAsync();

        // The name is stale: the access was taken away and the name is still there. Only the administrators hear.
        Assert.Equal(["admin", "owner"], await ToldAsync("collab-none"));
    }

    [Fact]
    public async Task UsersToTell_ACollaboratorSoloAlcuniWhoWasGivenTheProperty_InCharge_IsTold()
    {
        await SeedAsync();

        Assert.Equal(["admin", "collab-granted", "owner"], await ToldAsync("collab-granted"));
    }

    [Fact]
    public async Task UsersToTell_ACollaboratorWhoWasGivenOnlyAnotherProperty_InCharge_IsNotTold()
    {
        await SeedAsync();

        Assert.Equal(["admin", "owner"], await ToldAsync("collab-other"));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("accountant")]
    public async Task UsersToTell_AMemberWhoReachesEveryPropertyByItsRole_InCharge_IsTold(string name)
    {
        await SeedAsync();

        Assert.Equal(new[] { "admin", name, "owner" }.Order(StringComparer.Ordinal), await ToldAsync(name));
    }

    [Fact]
    public async Task UsersToTell_TheAdministrators_AreToldWhateverTheNameSays()
    {
        await SeedAsync();

        // A stale name, and a name that is nobody's.
        Assert.Equal(["admin", "owner"], await ToldAsync("collab-none"));
        Assert.Equal(["admin", "owner"], await ToldAsync("nobody-here"));
    }

    [Fact]
    public async Task UsersToTell_NobodyNamed_TheCreatorStandsInOnlyWhileItReachesTheProperty()
    {
        await SeedAsync();

        // A manager, a collaborator given the property and a collaborator with every property reach it.
        Assert.Equal(["admin", "manager", "owner"], await ToldAsync(null, "manager"));
        Assert.Equal(["admin", "collab-granted", "owner"], await ToldAsync(null, "collab-granted"));
        Assert.Equal(["admin", "collab-all", "owner"], await ToldAsync(null, "collab-all"));
        // A collaborator who made the property (it was a manager before it was made a collaborator) and was not given it does not.
        Assert.Equal(["admin", "owner"], await ToldAsync(null, "collab-none"));
    }

    [Fact]
    public async Task UsersToTell_AnAccountInNoTeam_IsToldAboutThePropertyItCreated_AsItAlwaysWas()
    {
        await SeedAsync();

        Assert.Equal(["admin", "legacy", "owner"], await ToldAsync(null, "legacy"));
    }

    [Fact]
    public async Task UsersToTell_ANameWithNoMemberRowThatIsNotTheCreator_FailsClosed()
    {
        await SeedAsync();

        // A person who is in no team can only have been put in charge by hand: nobody but the administrators is told.
        Assert.Equal(["admin", "owner"], await ToldAsync("legacy", "owner"));
    }

    [Fact]
    public async Task UsersToTell_AMemberRowOfAnotherOrg_IsNotToldAboutThisOnesProperty()
    {
        await SeedAsync();
        var other = new OrgEntity { Name = "Altro", Slug = $"altro-{Guid.NewGuid():N}"[..20], DisplayName = "Altro" };
        _db.Orgs.Add(other);
        AddPerson("confused", OrgRole.PropertyManager, memberOfOrg: other.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(["admin", "owner"], await ToldAsync("confused"));
    }

    [Fact]
    public async Task UsersToTell_ADeactivatedMember_IsNeverTold_WhateverItsRole()
    {
        await SeedAsync();
        AddPerson("manager-away", OrgRole.PropertyManager, status: OrgMemberStatus.Deactivated);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(["admin", "owner"], await ToldAsync("manager-away"));
    }

    // --- The emails -------------------------------------------------------------------------------------

    private BookingNotifier Notifier() =>
        new(_db, _emails, EmailTestHelpers.Links(), _pushes, NullLogger<BookingNotifier>.Instance);

    private async Task<List<string?>> HostRecipientsAsync()
    {
        _emails.Queued.Clear();
        await Notifier().BookingConfirmedAsync(_bookingId, BookingConfirmationKind.PaidOnline);
        return _emails.Snapshot()
            .Where(e => e.Template == EmailTemplates.Names.HostBookingConfirmed)
            .Select(e => e.To)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task BookingConfirmedAsync_ACollaboratorWhoLostThePropertyButIsStillNamed_IsNotEmailedTheGuestAndThePrices()
    {
        await SeedAsync();
        await NameAsync("collab-none");

        var recipients = await HostRecipientsAsync();

        // The contact of the org, the owner and the administrator: not the collaborator.
        Assert.Equal(["admin@example.com", "contatto@example.com", "owner@example.com"], recipients);
        Assert.DoesNotContain(_emails.Snapshot(), e => e.To == "collab-none@example.com");
    }

    [Fact]
    public async Task BookingConfirmedAsync_TheSameCollaboratorGivenThePropertyBack_IsEmailedAgain()
    {
        await SeedAsync();
        await NameAsync("collab-none");
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = _orgId, UserId = Id("collab-none"), PropertyId = _propertyId });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var recipients = await HostRecipientsAsync();

        Assert.Equal(["admin@example.com", "collab-none@example.com", "contatto@example.com", "owner@example.com"], recipients);
    }

    [Fact]
    public async Task BookingConfirmedAsync_AGrantOnAnotherPropertyIsNotEnough()
    {
        await SeedAsync();
        await NameAsync("collab-other");

        var recipients = await HostRecipientsAsync();

        Assert.DoesNotContain("collab-other@example.com", recipients);
    }

    // --- The push ---------------------------------------------------------------------------------------

    private PushDeliveryJob Job() => new(
        _db,
        _expo,
        new FakeTimeProvider(new DateTimeOffset(Now)),
        NullLogger<PushDeliveryJob>.Instance);

    private async Task<string[]> PushedTokensAsync(PushAudience audience, string key)
    {
        var before = _expo.Messages.Count;
        await Job().SendAsync(
            key,
            QueuedPush.From(audience, new PushNotificationPayload("Titolo", "Testo", PushTypes.NewBooking, null, PushRoutes.Properties)),
            CancellationToken.None);
        return _expo.Messages.Skip(before).Select(m => m.To).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task SendAsync_PropertyHosts_ACollaboratorWhoLostThePropertyButIsStillNamed_GetsNoPush()
    {
        await SeedAsync();
        await NameAsync("collab-none");

        var tokens = await PushedTokensAsync(PushAudience.PropertyHosts(_propertyId), "key-property");

        Assert.Equal(["ExponentPushToken[admin]", "ExponentPushToken[owner]"], tokens);
    }

    [Fact]
    public async Task SendAsync_BookingHosts_ACollaboratorWhoLostThePropertyButIsStillNamed_GetsNoPush()
    {
        await SeedAsync();
        await NameAsync("collab-none");

        var tokens = await PushedTokensAsync(PushAudience.BookingHosts(_bookingId), "key-booking");

        Assert.Equal(["ExponentPushToken[admin]", "ExponentPushToken[owner]"], tokens);
    }

    [Fact]
    public async Task SendAsync_TheCollaboratorGivenThePropertyAndNamed_StillGetsThePush()
    {
        await SeedAsync();
        await NameAsync("collab-granted");

        var tokens = await PushedTokensAsync(PushAudience.PropertyHosts(_propertyId), "key-granted");

        Assert.Equal(["ExponentPushToken[admin]", "ExponentPushToken[collab-granted]", "ExponentPushToken[owner]"], tokens);
    }

    // --- The whole journey ------------------------------------------------------------------------------

    [Fact]
    public async Task Journey_TheOwnerTakesThePropertyAway_ThePersonInChargeIsNoLongerToldByEitherChannel()
    {
        await SeedAsync();
        var access = new AccessWriter(_db);
        // The collaborator is in charge of the property it was given.
        await access.SetResponsibleAsync(_orgId, _propertyId, "collab-granted");
        Assert.Contains("collab-granted@example.com", await HostRecipientsAsync());
        Assert.Contains("ExponentPushToken[collab-granted]", await PushedTokensAsync(PushAudience.PropertyHosts(_propertyId), "key-before"));

        // The owner takes the property away.
        await access.NarrowAsync(_orgId, "collab-granted", _otherPropertyId);

        Assert.DoesNotContain("collab-granted@example.com", await HostRecipientsAsync());
        Assert.DoesNotContain("ExponentPushToken[collab-granted]", await PushedTokensAsync(PushAudience.PropertyHosts(_propertyId), "key-after"));
        Assert.Null((await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == _propertyId)).ResponsibleUserId);
    }

    /// <summary>The access service over the same context as the rest of the test, as one instance of the API would run it.</summary>
    private sealed class AccessWriter(AppDbContext db)
    {
        private OrgPropertyAccessService Access() => new(
            db,
            new Mock<IUserAuthorizationCache>().Object,
            NullLogger<OrgPropertyAccessService>.Instance);

        public async Task SetResponsibleAsync(Guid orgId, Guid propertyId, string name)
        {
            await Access().SetResponsibleAsync(orgId, propertyId, Id(name));
            db.ChangeTracker.Clear();
        }

        public async Task NarrowAsync(Guid orgId, string name, params Guid[] keep)
        {
            var memberId = (await db.OrgMembers.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.UserId == Id(name))).Id;
            await Access().SetAsync(orgId, memberId, Id("owner"), PropertyScope.Selected, keep);
            db.ChangeTracker.Clear();
        }
    }
}
