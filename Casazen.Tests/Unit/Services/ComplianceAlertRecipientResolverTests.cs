using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12), the proof of the permission rule: an alert about a property is delivered only to the people who reach it. A collaborator
/// «Solo alcuni» is among the recipients only for a property it was given; the address of the organization is the one every stay alert always used; the
/// answer holds ids and addresses and nothing else. EF InMemory; the audience is the one of <c>INotificationPreferenceService</c> (<c>HostNotificationAudience</c>).
/// </summary>
public class ComplianceAlertRecipientResolverTests : IDisposable
{
    private const string Owner = "auth0|owner";
    private const string Admin = "auth0|admin";
    private const string Manager = "auth0|manager";
    private const string CollaboratorGranted = "auth0|collab-granted";
    private const string CollaboratorNone = "auth0|collab-none";
    private const string CollaboratorOther = "auth0|collab-other";
    private const string OrgAddress = "contatto@example.com";

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly Mock<IContextAuthorizationService> _permissions = new();

    public void Dispose() => _db.Dispose();

    private ComplianceAlertRecipientResolver Resolver() =>
        new(
            _db,
            new NotificationPreferenceService(
                _db,
                _permissions.Object,
                new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero)),
                NullLogger<NotificationPreferenceService>.Instance));

    /// <summary>
    /// A host org with its contact address and a team: an owner, an administrator, a manager, and three collaborators «Solo alcuni» (one given the property A,
    /// one given nothing, one given the property A too). The property A is in charge of the first, B of the one given nothing (a stale name), C of the third,
    /// which was not given it.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        org.ContactEmail = OrgAddress;
        foreach (var id in new[] { Owner, Admin, Manager, CollaboratorGranted, CollaboratorNone, CollaboratorOther })
            OrgTeamTestData.AddUser(_db, id, org.Id);

        OrgTeamTestData.AddMember(_db, Owner, org.Id, OrgRole.Owner);
        OrgTeamTestData.AddMember(_db, Admin, org.Id, OrgRole.Admin);
        OrgTeamTestData.AddMember(_db, Manager, org.Id, OrgRole.PropertyManager);
        foreach (var id in new[] { CollaboratorGranted, CollaboratorNone, CollaboratorOther })
            OrgTeamTestData.AddMember(_db, id, org.Id, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;

        var a = NewProperty(org.Id, "Casa A", CollaboratorGranted);
        var b = NewProperty(org.Id, "Casa B", CollaboratorNone);
        var c = NewProperty(org.Id, "Casa C", CollaboratorOther);
        _db.Properties.AddRange(a, b, c);
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = CollaboratorGranted, PropertyId = a.Id });
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = CollaboratorOther, PropertyId = a.Id });
        await _db.SaveChangesAsync();
        return new World(org.Id, a.Id, b.Id, c.Id);
    }

    private static Property NewProperty(Guid orgId, string name, string responsible) => new()
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

    [Fact]
    public async Task ComplianceAlerts_RecipientsLimitedToHostScope_Test()
    {
        var world = await SeedAsync();
        var resolver = Resolver();

        // The property A: the collaborator in charge of it is given it, so it hears; the one only given A without being in charge does not (it is not the
        // audience of the property); the administrators always do.
        var a = await resolver.ForPropertyAsync(world.OrgId, world.PropertyA, CancellationToken.None);
        Assert.Equal([Admin, CollaboratorGranted, Owner], a.UserIds.Order(StringComparer.Ordinal).ToArray());

        // The property B: its person in charge lost the access (a stale name): only the administrators, never the collaborator or the other ones.
        var b = await resolver.ForPropertyAsync(world.OrgId, world.PropertyB, CancellationToken.None);
        Assert.Equal([Admin, Owner], b.UserIds.Order(StringComparer.Ordinal).ToArray());

        // The property C: its person in charge was given another property, not this one.
        var c = await resolver.ForPropertyAsync(world.OrgId, world.PropertyC, CancellationToken.None);
        Assert.Equal([Admin, Owner], c.UserIds.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(CollaboratorOther, c.UserIds);
        Assert.DoesNotContain(Manager, c.UserIds);
    }

    [Fact]
    public async Task ForPropertyAsync_TheEmailIsTheAddressOfTheOrganizationOnly_NotThatOfAnyCollaborator()
    {
        var world = await SeedAsync();

        var recipients = await Resolver().ForPropertyAsync(world.OrgId, world.PropertyB, CancellationToken.None);

        Assert.Equal(OrgAddress, Assert.Single(recipients.Emails));
        var addresses = _db.Users.Where(u => u.Id == CollaboratorNone || u.Id == CollaboratorOther).Select(u => u.Email).ToList();
        Assert.All(addresses, address => Assert.DoesNotContain(address, recipients.Emails));
    }

    [Fact]
    public async Task ForPropertyAsync_ADeactivatedMember_IsNeverAmongTheRecipients()
    {
        var world = await SeedAsync();
        var member = _db.OrgMembers.Single(m => m.UserId == CollaboratorGranted);
        member.Status = OrgMemberStatus.Deactivated;
        await _db.SaveChangesAsync();

        var recipients = await Resolver().ForPropertyAsync(world.OrgId, world.PropertyA, CancellationToken.None);

        Assert.DoesNotContain(CollaboratorGranted, recipients.UserIds);
        Assert.Contains(Owner, recipients.UserIds);
    }

    [Fact]
    public async Task ForPropertyAsync_APropertyOfAnotherOrg_HasNoPeople()
    {
        var world = await SeedAsync();
        var other = OrgTeamTestData.AddOrg(_db);
        await _db.SaveChangesAsync();

        var recipients = await Resolver().ForPropertyAsync(other.Id, world.PropertyA, CancellationToken.None);

        Assert.Empty(recipients.UserIds);
    }

    [Fact]
    public async Task ForOrganizationAsync_IsTheAddressOfTheOrganizationAndTheAdministrators_NotTheManagerNorACollaborator()
    {
        var world = await SeedAsync();

        var recipients = await Resolver().ForOrganizationAsync(world.OrgId, CancellationToken.None);

        Assert.Equal([Admin, Owner], recipients.UserIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(OrgAddress, recipients.Emails);
        var administrators = _db.Users.Where(u => u.Id == Owner || u.Id == Admin).Select(u => u.Email).ToList();
        Assert.Equal(
            administrators.Append(OrgAddress).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            recipients.Emails.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var others = _db.Users.Where(u => u.Id == Manager || u.Id == CollaboratorGranted).Select(u => u.Email).ToList();
        Assert.All(others, address => Assert.DoesNotContain(address, recipients.Emails));
    }
}
