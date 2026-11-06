using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F03/US01/T02 (NF-65): the settings of a user are saved and read back, an event is resolved to recipients and channels from them, and the
/// proofs of the brief — a collaborator «Solo alcuni» is never told about a property it was not given, and a legal deadline is never fully off.
/// EF InMemory: <c>Hangfire</c>, SignalR and the real authorization are not involved (the permission is a mock).
/// </summary>
public class NotificationPreferenceServiceTests : IDisposable
{
    private const string Owner = "auth0|owner";
    private const string Collaborator = "auth0|collaborator";
    private const string Manager = "auth0|manager";

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero)); // 12:00 in Rome
    private readonly Mock<IContextAuthorizationService> _permissions = new();

    public NotificationPreferenceServiceTests()
    {
        _permissions
            .Setup(p => p.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Dispose() => _db.Dispose();

    private NotificationPreferenceService Service() =>
        new(_db, _permissions.Object, _clock, NullLogger<NotificationPreferenceService>.Instance);

    /// <summary>
    /// A host org with an owner, a collaborator limited to the property A (named in charge of A and of B, granted A only) and a manager in charge
    /// of nothing; the properties A, B and C (C has nobody in charge: its creator, the owner, stands in).
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        OrgTeamTestData.AddUser(_db, Owner, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddUser(_db, Collaborator, org.Id, UserRole.None);
        OrgTeamTestData.AddUser(_db, Manager, org.Id, UserRole.None);
        OrgTeamTestData.AddMember(_db, Owner, org.Id, OrgRole.Owner);
        OrgTeamTestData.AddMember(_db, Collaborator, org.Id, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;
        OrgTeamTestData.AddMember(_db, Manager, org.Id, OrgRole.PropertyManager);

        var a = NewProperty(org.Id, "Casa A", Collaborator);
        var b = NewProperty(org.Id, "Casa B", Collaborator);
        var c = NewProperty(org.Id, "Casa C", null);
        _db.Properties.AddRange(a, b, c);
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Collaborator, PropertyId = a.Id });
        await _db.SaveChangesAsync();
        return new World(org.Id, a.Id, b.Id, c.Id);
    }

    private static Property NewProperty(Guid orgId, string name, string? responsible) => new()
    {
        OrgId = orgId,
        OwnerId = Owner,
        ResponsibleUserId = responsible,
        Name = name,
        Address = "Via Test 1",
        City = "Roma",
        PostalCode = "00100",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 100m,
    };

    private sealed record World(Guid OrgId, Guid PropertyA, Guid PropertyB, Guid PropertyC);

    private static NotificationEventContext Event(Guid orgId, Guid? propertyId, NotificationEventType type = NotificationEventType.NewGuestMessage) =>
        new(orgId, type, propertyId);

    // ─── Get and Save (AC-NF65-1, AC-NF65-2) ───

    [Fact]
    public async Task Get_ANewUser_GetsTheDefaultsOfPo10_MarkedAsDefaults()
    {
        await SeedAsync();

        var view = await Service().GetAsync(Owner, CancellationToken.None);

        Assert.Equal(8, view.Events.Count);
        Assert.All(view.Events, e => Assert.True(e.IsDefault));
        var message = Assert.Single(view.Events, e => e.EventType == NotificationEventType.NewGuestMessage);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.CounterOnly, message.Channels);
        Assert.Equal(new TimeOnly(22, 0), view.QuietHours.Start);
        Assert.Equal(new TimeOnly(8, 0), view.QuietHours.End);
        Assert.True(view.QuietHours.LegalAlertsOverride);
        Assert.Equal(0, await _db.UserNotificationChannelSettings.CountAsync());
    }

    [Fact]
    public async Task Get_BrowserPush_IsReportedUnavailable()
    {
        await SeedAsync();

        var view = await Service().GetAsync(Owner, CancellationToken.None);

        var browser = Assert.Single(view.AvailableChannels, c => c.Channel == NotificationChannels.BrowserPush);
        Assert.False(browser.Available);
        Assert.Equal(NotificationPreferenceService.WebPushNotAvailableReason, browser.UnavailableReason);
        Assert.All(view.AvailableChannels.Where(c => c.Channel != NotificationChannels.BrowserPush), c => Assert.True(c.Available));
    }

    [Fact]
    public async Task Save_TheChoicesAndTheQuietHours_AreReadBack_AndTheOtherEventsStayDefault()
    {
        await SeedAsync();

        var saved = await Service().SaveAsync(
            Owner,
            new NotificationPreferencesUpdate(
                new QuietHoursUpdate(new TimeOnly(23, 30), new TimeOnly(7, 0)),
                [new NotificationEventUpdate(NotificationEventType.NewGuestMessage, NotificationChannels.Email | NotificationChannels.CounterOnly)]),
            CancellationToken.None);
        var again = await Service().GetAsync(Owner, CancellationToken.None);

        Assert.Equal(saved.Events, again.Events);
        var message = Assert.Single(again.Events, e => e.EventType == NotificationEventType.NewGuestMessage);
        Assert.Equal(NotificationChannels.Email | NotificationChannels.CounterOnly, message.Channels);
        Assert.False(message.IsDefault);
        Assert.Equal(7, again.Events.Count(e => e.IsDefault));
        Assert.Equal(new TimeOnly(23, 30), again.QuietHours.Start);
        Assert.Equal(new TimeOnly(7, 0), again.QuietHours.End);
    }

    [Fact]
    public async Task Save_AgainForTheSameEvent_ChangesTheRow_NeverAddsASecond()
    {
        await SeedAsync();
        var service = Service();

        await service.SaveAsync(Owner, Update(NotificationEventType.NewBooking, NotificationChannels.Email), CancellationToken.None);
        await service.SaveAsync(Owner, Update(NotificationEventType.NewBooking, NotificationChannels.None), CancellationToken.None);

        var row = await _db.UserNotificationChannelSettings.SingleAsync();
        Assert.Equal(NotificationChannels.None, row.Channels);
        Assert.Equal(NotificationEventType.NewBooking, row.EventType);
    }

    [Fact]
    public async Task Save_EachUserHasItsOwnChoices()
    {
        await SeedAsync();
        var service = Service();

        await service.SaveAsync(Owner, Update(NotificationEventType.NewBooking, NotificationChannels.Email), CancellationToken.None);

        var other = await service.GetAsync(Manager, CancellationToken.None);
        Assert.All(other.Events, e => Assert.True(e.IsDefault));
    }

    [Fact]
    public async Task Save_BrowserPush_IsRefused_AndNothingIsWritten()
    {
        await SeedAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(
            Owner,
            new NotificationPreferencesUpdate(
                new QuietHoursUpdate(new TimeOnly(21, 0), new TimeOnly(6, 0)),
                [new NotificationEventUpdate(NotificationEventType.NewGuestMessage, NotificationChannels.BrowserPush | NotificationChannels.Email)]),
            CancellationToken.None));

        Assert.Equal("notification_channel_unavailable", error.Code);
        Assert.Equal(0, await _db.UserNotificationChannelSettings.CountAsync());
        Assert.Equal(0, await _db.UserNotificationPreferences.CountAsync());
    }

    [Fact]
    public async Task Save_NothingToSave_IsAnError()
    {
        await SeedAsync();

        var none = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(Owner, new NotificationPreferencesUpdate(null, null), CancellationToken.None));
        var emptyList = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(Owner, new NotificationPreferencesUpdate(null, []), CancellationToken.None));

        // An empty list never means «everything»: it is nothing to save.
        Assert.Equal("notification_preferences_empty", none.Code);
        Assert.Equal("notification_preferences_empty", emptyList.Code);
    }

    [Fact]
    public async Task Save_TheSameEventTwice_IsAnError()
    {
        await SeedAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service().SaveAsync(
            Owner,
            new NotificationPreferencesUpdate(
                null,
                [
                    new NotificationEventUpdate(NotificationEventType.NewBooking, NotificationChannels.Email),
                    new NotificationEventUpdate(NotificationEventType.NewBooking, NotificationChannels.CounterOnly),
                ]),
            CancellationToken.None));

        Assert.Equal("notification_event_duplicated", error.Code);
    }

    [Fact]
    public async Task Save_AUserWithNoHostOrg_Is404_AndGetGivesTheDefaults()
    {
        var supplierOrg = OrgTeamTestData.AddOrg(_db, OrgType.Supplier);
        OrgTeamTestData.AddUser(_db, "auth0|supplier", supplierOrg.Id);
        await _db.SaveChangesAsync();

        var view = await Service().GetAsync("auth0|supplier", CancellationToken.None);
        await Assert.ThrowsAsync<NotFoundException>(
            () => Service().SaveAsync("auth0|supplier", Update(NotificationEventType.NewBooking, NotificationChannels.Email), CancellationToken.None));

        Assert.All(view.Events, e => Assert.True(e.IsDefault));
        Assert.Equal(0, await _db.UserNotificationChannelSettings.CountAsync());
    }

    // ─── Proof: a legal deadline is never fully off ───

    [Fact]
    public async Task NotificationPreferences_LegalDeadline_NeverFullyOff_Test()
    {
        var world = await SeedAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().SaveAsync(Owner, Update(NotificationEventType.LegalDeadline, NotificationChannels.None), CancellationToken.None));

        Assert.Equal("notification_legal_channel_required", error.Code);
        Assert.Equal(0, await _db.UserNotificationChannelSettings.CountAsync());

        // Not even a row written some other way (by hand, an old release) leaves the person without the alert: the default applies.
        _db.UserNotificationChannelSettings.Add(new UserNotificationChannelSetting
        {
            OrgId = world.OrgId,
            UserId = Owner,
            EventType = NotificationEventType.LegalDeadline,
            Channels = NotificationChannels.None,
        });
        await _db.SaveChangesAsync();

        var view = await Service().GetAsync(Owner, CancellationToken.None);
        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC, NotificationEventType.LegalDeadline), CancellationToken.None);

        Assert.NotEqual(
            NotificationChannels.None,
            Assert.Single(view.Events, e => e.EventType == NotificationEventType.LegalDeadline).Channels);
        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.NotEqual(NotificationChannels.None, owner.NowChannels);
    }

    // ─── Resolve: who is told ───

    [Fact]
    public async Task Resolve_ADefaultUser_GetsPushAndCounterForANewGuestMessage()
    {
        var world = await SeedAsync();

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.CounterOnly, owner.NowChannels);
        Assert.Equal(NotificationChannels.None, owner.DeferredChannels);
        Assert.Null(owner.DueAtUtc);
    }

    [Fact]
    public async Task Resolve_TheChoicesOfTheUser_AreRespected()
    {
        var world = await SeedAsync();
        await Service().SaveAsync(
            Owner,
            Update(NotificationEventType.NewGuestMessage, NotificationChannels.Email | NotificationChannels.CounterOnly),
            CancellationToken.None);

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.Email | NotificationChannels.CounterOnly, owner.NowChannels);
    }

    [Fact]
    public async Task Resolve_AUserWhoChoseNothing_IsNotARecipient()
    {
        var world = await SeedAsync();
        await Service().SaveAsync(Owner, Update(NotificationEventType.NewGuestMessage, NotificationChannels.None), CancellationToken.None);

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        Assert.DoesNotContain(recipients, r => r.UserId == Owner);
    }

    [Fact]
    public async Task Resolve_TheEmailIsThePersonsOwn_AndAnAccountWithoutOneIsNotEmailed()
    {
        var world = await SeedAsync();
        await Service().SaveAsync(
            Owner,
            Update(NotificationEventType.NewGuestMessage, NotificationChannels.Email | NotificationChannels.CounterOnly),
            CancellationToken.None);

        var ownerEmail = (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == Owner)).Email;
        var before = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var user = await _db.Users.SingleAsync(u => u.Id == Owner);
        user.Email = string.Empty;
        await _db.SaveChangesAsync();
        var after = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        Assert.Equal(ownerEmail, Assert.Single(before, r => r.UserId == Owner).Email);
        Assert.NotEqual(Owner, ownerEmail);
        var withoutEmail = Assert.Single(after, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.CounterOnly, withoutEmail.NowChannels);
    }

    // ─── Resolve: the scope of a collaborator (AC-NF65-4) ───

    [Fact]
    public async Task NotificationPreferences_CollaboratorWithSomeProperties_NeverToldAboutOthers_Test()
    {
        var world = await SeedAsync();
        var service = Service();

        // The collaborator is named in charge of both A and B, but it was given A only.
        var forTheGivenOne = await service.ResolveAsync(Event(world.OrgId, world.PropertyA), CancellationToken.None);
        var forTheOtherOne = await service.ResolveAsync(Event(world.OrgId, world.PropertyB), CancellationToken.None);

        Assert.Equal([Collaborator, Owner], forTheGivenOne.Select(r => r.UserId).Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(forTheOtherOne, r => r.UserId == Collaborator);
        Assert.Contains(forTheOtherOne, r => r.UserId == Owner);

        // Nor an event of the org, with no property, nor the property a caller names with the person in charge: the grants decide.
        var forTheOrg = await service.ResolveAsync(Event(world.OrgId, null), CancellationToken.None);
        var namedInCharge = await service.ResolveAsync(
            new NotificationEventContext(world.OrgId, NotificationEventType.NewGuestMessage, world.PropertyB, Collaborator, Owner),
            CancellationToken.None);
        Assert.DoesNotContain(forTheOrg, r => r.UserId == Collaborator);
        Assert.DoesNotContain(namedInCharge, r => r.UserId == Collaborator);
    }

    [Fact]
    public async Task Resolve_AnEventWithNoProperty_GoesToTheAdministratorsOnly()
    {
        var world = await SeedAsync();

        var recipients = await Service().ResolveAsync(Event(world.OrgId, null), CancellationToken.None);

        Assert.Equal([Owner], recipients.Select(r => r.UserId).ToList());
    }

    [Fact]
    public async Task Resolve_APropertyOfAnotherOrgOrAnUnknownOne_HasNoAudience()
    {
        var world = await SeedAsync();

        Assert.Empty(await Service().ResolveAsync(Event(world.OrgId, Guid.NewGuid()), CancellationToken.None));
        Assert.Empty(await Service().ResolveAsync(Event(Guid.NewGuid(), world.PropertyA), CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_ADeactivatedMember_IsNeverTold()
    {
        var world = await SeedAsync();
        _db.ChangeTracker.Clear();
        (await _db.OrgMembers.SingleAsync(m => m.UserId == Owner)).Status = OrgMemberStatus.Deactivated;
        await _db.SaveChangesAsync();

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        Assert.DoesNotContain(recipients, r => r.UserId == Owner);
    }

    [Fact]
    public async Task Resolve_ThePermissionNarrowsTheAudience()
    {
        var world = await SeedAsync();
        _permissions
            .Setup(p => p.HasPermissionAsync(Collaborator, "short-rent", "booking.read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var recipients = await Service().ResolveAsync(
            new NotificationEventContext(
                world.OrgId,
                NotificationEventType.NewGuestMessage,
                world.PropertyA,
                RequiredPermission: new NotificationPermission("short-rent", "booking.read")),
            CancellationToken.None);

        Assert.Equal([Owner], recipients.Select(r => r.UserId).ToList());
    }

    // ─── Resolve: quiet hours (AC-NF65-3) ───

    [Fact]
    public async Task Resolve_InTheQuietHours_PushWaitsAndTheCounterDoesNot()
    {
        var world = await SeedAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.Zero)); // 23:00 in Rome

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.CounterOnly, owner.NowChannels);
        Assert.Equal(NotificationChannels.MobilePush, owner.DeferredChannels);
        Assert.Equal(new DateTime(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc), owner.DueAtUtc);
    }

    [Fact]
    public async Task Resolve_TheInstantOfTheEventDecidesTheQuietHours_NotTheInstantOfTheProcessing()
    {
        var world = await SeedAsync();
        var night = new DateTime(2026, 11, 6, 22, 0, 0, DateTimeKind.Utc);

        // The clock says noon; the event happened at 23:00 in Rome: a redelivery splits the channels as the first delivery did.
        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC) with { AtUtc = night }, CancellationToken.None);

        Assert.Equal(NotificationChannels.MobilePush, Assert.Single(recipients, r => r.UserId == Owner).DeferredChannels);
    }

    [Fact]
    public async Task Resolve_TheQuietHoursOfTheUser_AreItsOwn()
    {
        var world = await SeedAsync();
        await Service().SaveAsync(Owner, new NotificationPreferencesUpdate(new QuietHoursUpdate(new TimeOnly(13, 0), new TimeOnly(14, 0)), null), CancellationToken.None);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 12, 30, 0, TimeSpan.Zero)); // 13:30 in Rome

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyC), CancellationToken.None);

        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.MobilePush, owner.DeferredChannels);
        Assert.Equal(new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc), owner.DueAtUtc);
    }

    [Fact]
    public async Task Resolve_ALegalDeadline_OverridesTheQuietHours()
    {
        var world = await SeedAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.Zero));

        var recipients = await Service().ResolveAsync(
            Event(world.OrgId, world.PropertyC, NotificationEventType.LegalDeadline),
            CancellationToken.None);

        var owner = Assert.Single(recipients, r => r.UserId == Owner);
        Assert.Equal(NotificationChannels.None, owner.DeferredChannels);
        Assert.True(owner.NowChannels.HasFlag(NotificationChannels.MobilePush));
        Assert.True(owner.NowChannels.HasFlag(NotificationChannels.Email));
    }

    [Fact]
    public async Task Resolve_OnlyUserId_NarrowsTheAudienceToThatPerson()
    {
        var world = await SeedAsync();

        var recipients = await Service().ResolveAsync(Event(world.OrgId, world.PropertyA) with { OnlyUserId = Collaborator }, CancellationToken.None);

        Assert.Equal([Collaborator], recipients.Select(r => r.UserId).ToList());
    }

    private static NotificationPreferencesUpdate Update(NotificationEventType type, NotificationChannels channels) =>
        new(null, [new NotificationEventUpdate(type, channels)]);
}
