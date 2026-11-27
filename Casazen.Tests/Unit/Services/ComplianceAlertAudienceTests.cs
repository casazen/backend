using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Push;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E30/F02/US01/T01 and E31/F02/US01/T01, the proof of the permission rule of the alerts about the notification of the guests to the police (Ticino, and the other cantons): they are told
/// only to the people who <b>can act on them</b>, the ones who hold <c>alloggiati.submit</c> (the permission of <c>CasazenPolicies.AlloggiatiSubmit</c>) on top of reaching the property. A
/// collaborator who does not hold it is not told in the app (no push, no entry of the bell), whatever its reach; the Italian deadline alerts keep the audience they always had. The services
/// are the real ones: <see cref="ComplianceAlertRecipientResolver"/> over <see cref="NotificationPreferenceService"/> over <see cref="ContextAuthorizationService"/> (the evaluation of a
/// permission the policies use, on the seeded roles and memberships), and the two jobs that tell the phone and the bell (<see cref="PushDeliveryJob"/>, <see cref="InAppNotificationJob"/>) on the
/// same audience. EF InMemory; the stand-ins are the Expo client and the publisher of the live update.
/// </summary>
public class ComplianceAlertAudienceTests : IDisposable
{
    private const string OrgAddress = "contatto@example.com";

    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeExpoPushClient _expo = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));

    private Guid _orgId;
    private Guid _propertyId;
    private Guid _otherPropertyId;

    public void Dispose() => _db.Dispose();

    private static string Id(string name) => $"auth0|{name}";

    private static LegalDocumentService Legal => new(new ConfigurationBuilder().Build(), NullLogger<LegalDocumentService>.Instance);

    /// <summary>The real evaluation of a permission, read fresh each time (no cache), as a job with no request would run it.</summary>
    private ContextAuthorizationService Permissions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authorization:UserCacheSeconds"] = "0" }).Build();
        var accessor = new HttpContextAccessor();
        return new ContextAuthorizationService(
            new UserAuthorizationSnapshotStore(_db, new MemoryCache(new MemoryCacheOptions()), accessor, configuration),
            Legal,
            accessor,
            NullLogger<ContextAuthorizationService>.Instance);
    }

    private ComplianceAlertRecipientResolver Resolver() =>
        new(_db, new NotificationPreferenceService(_db, Permissions(), _clock, NullLogger<NotificationPreferenceService>.Instance));

    private async Task AddPersonAsync(
        string name,
        OrgRole? orgRole,
        string? shortRentRole,
        PropertyScope scope = PropertyScope.All,
        OrgMemberStatus status = OrgMemberStatus.Active,
        bool onboarded = true,
        UserRole userRole = UserRole.None)
    {
        var user = OrgTeamTestData.AddUser(_db, Id(name), _orgId, userRole);
        user.Email = $"{name}@example.com";
        if (onboarded)
            await HostOnboardingSeed.MarkOnboardedAsync(_db, user, _orgId, Legal);

        if (orgRole is not null)
            OrgTeamTestData.AddMember(_db, user.Id, _orgId, orgRole.Value, status).PropertyScope = scope;
        if (shortRentRole is not null)
            OrgTeamTestData.AddMembership(_db, user.Id, "short-rent", shortRentRole);

        _db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = user.Id,
            OrgId = _orgId,
            Platform = "android",
            PushToken = $"ExponentPushToken[{name}]",
            DeviceId = Guid.NewGuid().ToString(),
        });
    }

    /// <summary>
    /// An org with its contact address and two properties, and the people of its team with the roles the product seeds: the owner, an administrator, a property manager (they hold
    /// <c>alloggiati.submit</c>), an accountant and a collaborator (they do not); a collaborator «Solo alcuni» given the first property with no license, one given it WITH the license
    /// (the matrix of a role may give it), one with the license given only the second property, one with the license who was deactivated; an administrator who never finished the onboarding
    /// (the host contexts are withheld: no permission) and an account in no team that created the property (the owner on its token, before the team).
    /// </summary>
    private async Task SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        org.ContactEmail = OrgAddress;
        _orgId = org.Id;

        await AddPersonAsync("owner", OrgRole.Owner, "property_owner");
        await AddPersonAsync("admin", OrgRole.Admin, "property_manager");
        await AddPersonAsync("manager", OrgRole.PropertyManager, "property_manager");
        await AddPersonAsync("accountant", OrgRole.Accountant, "accountant");
        await AddPersonAsync("collab", OrgRole.Collaborator, "staff", PropertyScope.Selected);
        await AddPersonAsync("collab-licensed", OrgRole.Collaborator, "property_manager", PropertyScope.Selected);
        await AddPersonAsync("collab-licensed-other", OrgRole.Collaborator, "property_manager", PropertyScope.Selected);
        await AddPersonAsync("collab-deactivated", OrgRole.Collaborator, "property_manager", PropertyScope.Selected, OrgMemberStatus.Deactivated);
        await AddPersonAsync("admin-not-onboarded", OrgRole.Admin, "property_manager", onboarded: false);
        await AddPersonAsync("legacy", orgRole: null, shortRentRole: null, userRole: UserRole.PropertyOwner);

        var property = NewProperty(org.Id, "Casa Lugano", "owner");
        var other = NewProperty(org.Id, "Casa Locarno", "owner");
        _db.Properties.AddRange(property, other);
        await _db.SaveChangesAsync();

        foreach (var name in new[] { "collab", "collab-licensed", "collab-deactivated" })
            _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Id(name), PropertyId = property.Id });
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Id("collab-licensed-other"), PropertyId = other.Id });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        (_propertyId, _otherPropertyId) = (property.Id, other.Id);
    }

    private static Property NewProperty(Guid orgId, string name, string creator) => new()
    {
        OrgId = orgId,
        OwnerId = Id(creator),
        Name = name,
        Address = $"Via {name} 1",
        City = "Lugano",
        PostalCode = "6900",
        Country = "CH",
        Currency = "CHF",
        Timezone = "Europe/Zurich",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 100m,
    };

    /// <summary>Who is in charge of a property (the first one unless said) and who created it: a stale name is a name the person no longer reaches.</summary>
    private async Task NameAsync(string? responsible, string creator = "owner", Guid? propertyId = null)
    {
        var property = await _db.Properties.SingleAsync(p => p.Id == (propertyId ?? _propertyId));
        property.ResponsibleUserId = responsible is null ? null : Id(responsible);
        property.OwnerId = Id(creator);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static string[] Names(IEnumerable<string> userIds) =>
        userIds.Select(id => id.Replace("auth0|", string.Empty, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static string[] Of(string names) => names.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();

    // ─── The one definition ───

    [Fact]
    public void RequiredPermissionOf_IsTheVerySamePermissionAsThePolicyOfTheDeclaration_ForTheTwoPoliceNotificationAlertsOnly()
    {
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.AlloggiatiSubmit);
        var expected = new NotificationPermission(Assert.Single(contexts), permission);

        Assert.Equal(expected, ComplianceAlertAudience.AlloggiatiSubmit);
        Assert.Equal(expected, ComplianceAlertAudience.RequiredPermissionOf(ComplianceAlertType.TicinoNotification));
        Assert.Equal(expected, ComplianceAlertAudience.RequiredPermissionOf(ComplianceAlertType.SwissNotification));

        // The alerts of the Italian periods and of the connection keep the audience they always had.
        var others = Enum.GetValues<ComplianceAlertType>().Where(t => t is not (ComplianceAlertType.TicinoNotification or ComplianceAlertType.SwissNotification)).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, type => Assert.Null(ComplianceAlertAudience.RequiredPermissionOf(type)));
    }

    // ─── The audience: reach AND permission ───

    // The people told about the alert of the police, and about an alert of the Italian deadlines, for each person in charge of the property. A collaborator without the license is told about the
    // Italian alert and not about the police one; the administrators and the property manager hold it and are told about both; whoever does not reach the property (a name that is stale, a member
    // who was deactivated) is told about neither, license or not; an administrator whose onboarding is not finished holds no permission, so the Italian alert reaches it and the police one does not.
    [Theory]
    [InlineData("collab", "admin|admin-not-onboarded|collab|owner", "admin|owner")]
    [InlineData("manager", "admin|admin-not-onboarded|manager|owner", "admin|manager|owner")]
    [InlineData("accountant", "accountant|admin|admin-not-onboarded|owner", "admin|owner")]
    [InlineData("collab-licensed", "admin|admin-not-onboarded|collab-licensed|owner", "admin|collab-licensed|owner")]
    [InlineData("collab-licensed-other", "admin|admin-not-onboarded|owner", "admin|owner")]
    [InlineData("collab-deactivated", "admin|admin-not-onboarded|owner", "admin|owner")]
    public async Task APoliceNotificationAlert_IsToldOnlyToWhoHoldsTheLicense_OnTopOfTheReach(string inCharge, string italianAudience, string policeAudience)
    {
        await SeedAsync();
        await NameAsync(inCharge);
        var resolver = Resolver();

        var italian = await resolver.ForPropertyAsync(_orgId, _propertyId, CancellationToken.None);
        Assert.Equal(Of(italianAudience), Names(italian.UserIds));

        foreach (var type in new[] { ComplianceAlertType.TicinoNotification, ComplianceAlertType.SwissNotification })
        {
            var police = await resolver.ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.RequiredPermissionOf(type)!, CancellationToken.None);

            Assert.Equal(Of(policeAudience), Names(police.UserIds));

            // The e-mail is the address of the organization, whoever holds the permission: it is not a person.
            Assert.Equal(OrgAddress, Assert.Single(police.Emails));
            Assert.Equal(italian.Emails, police.Emails);
        }
    }

    [Fact]
    public async Task APoliceNotificationAlert_ACollaboratorWhoHoldsTheLicenseButWasNotGivenTheProperty_IsNotTold()
    {
        await SeedAsync();
        await NameAsync("collab-licensed-other");

        // The collaborator holds the permission and was given the OTHER property: the reach decides, the permission only narrows it.
        var police = await Resolver().ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);
        Assert.DoesNotContain("collab-licensed-other", Names(police.UserIds));

        // Put in charge of the property it was given, the same person is told: the permission and the grant both hold.
        await NameAsync("collab-licensed-other", propertyId: _otherPropertyId);
        var own = await Resolver().ForPropertyAsync(_orgId, _otherPropertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);
        Assert.Equal(Of("admin|collab-licensed-other|owner"), Names(own.UserIds));
    }

    [Fact]
    public async Task APoliceNotificationAlert_NobodyNamed_TheCreatorWhoIsInNoTeamHoldsTheLicenseOnItsToken()
    {
        await SeedAsync();
        await NameAsync(null, creator: "legacy");

        var police = await Resolver().ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);

        // The owner of before the team holds the license as the policy gives it to it (the role of its account), so it is told, with the administrators.
        Assert.Equal(Of("admin|legacy|owner"), Names(police.UserIds));
    }

    [Fact]
    public async Task APoliceNotificationAlert_AnAdministratorWhoWasDeactivatedAfterItHeldTheLicense_IsNoLongerTold()
    {
        await SeedAsync();
        var admin = await _db.OrgMembers.SingleAsync(m => m.UserId == Id("admin"));
        admin.Status = OrgMemberStatus.Deactivated;
        await _db.SaveChangesAsync();

        var police = await Resolver().ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);

        Assert.DoesNotContain("admin", Names(police.UserIds));
        Assert.Contains("owner", Names(police.UserIds));
    }

    [Fact]
    public async Task APoliceNotificationAlert_APropertyOfAnotherOrg_HasNoPeople()
    {
        await SeedAsync();
        var other = OrgTeamTestData.AddOrg(_db);
        await _db.SaveChangesAsync();

        var police = await Resolver().ForPropertyAsync(other.Id, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);

        Assert.Empty(police.UserIds);
    }

    // ─── The phone and the bell tell the same people ───

    private InAppNotificationJob Bell(bool withTheEvaluation = true) =>
        new(_db, _clock, new NoOpInAppNotificationPublisher(), NullLogger<InAppNotificationJob>.Instance, withTheEvaluation ? Permissions() : null);

    private PushDeliveryJob Phone(bool withTheEvaluation = true) =>
        new(_db, _expo, _clock, NullLogger<PushDeliveryJob>.Instance, withTheEvaluation ? Permissions() : null);

    private static PushNotificationPayload Alert() => new("Titolo", "Testo", PushTypes.TicinoNotificationDue, null, PushRoutes.Properties);

    /// <summary>The people the bell writes an entry for, for an event of this audience.</summary>
    private async Task<string[]> ToldByTheBellAsync(PushAudience audience, string key, bool withTheEvaluation = true)
    {
        await Bell(withTheEvaluation).CreateAsync(key, QueuedInAppNotification.From(audience, Alert(), Now), CancellationToken.None);
        return Names(await _db.InAppNotifications.AsNoTracking().Where(n => n.DeliveryKey == key).Select(n => n.UserId).ToListAsync());
    }

    /// <summary>The people whose phone is pushed, for an event of this audience.</summary>
    private async Task<string[]> ToldByThePhoneAsync(PushAudience audience, string key, bool withTheEvaluation = true)
    {
        var before = _expo.Messages.Count;
        await Phone(withTheEvaluation).SendAsync(key, QueuedPush.From(audience, Alert()), CancellationToken.None);
        return _expo.Messages.Skip(before).Select(m => m.To.Replace("ExponentPushToken[", string.Empty, StringComparison.Ordinal).TrimEnd(']')).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task ThePhoneAndTheBell_OfAPoliceNotificationAlert_TellExactlyThePeopleOfTheAudience_NotTheCollaboratorWithoutTheLicense()
    {
        await SeedAsync();
        await NameAsync("collab");
        var police = PushAudience.PropertyHosts(_propertyId, ComplianceAlertAudience.AlloggiatiSubmit);

        var audience = await Resolver().ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);

        Assert.Equal(Of("admin|owner"), Names(audience.UserIds));
        Assert.Equal(Names(audience.UserIds), await ToldByTheBellAsync(police, "police-bell"));
        Assert.Equal(Names(audience.UserIds), await ToldByThePhoneAsync(police, "police-phone"));
    }

    [Fact]
    public async Task ThePhoneAndTheBell_OfAnItalianDeadlineAlert_StillTellTheCollaboratorWhoIsInCharge()
    {
        await SeedAsync();
        await NameAsync("collab");
        var italian = PushAudience.PropertyHosts(_propertyId);

        // Nothing changes for the audience with no required permission: the person in charge, the administrators (whatever their onboarding) and nobody else.
        Assert.Equal(Of("admin|admin-not-onboarded|collab|owner"), await ToldByTheBellAsync(italian, "italian-bell"));
        Assert.Equal(Of("admin|admin-not-onboarded|collab|owner"), await ToldByThePhoneAsync(italian, "italian-phone"));
    }

    [Fact]
    public async Task ThePhoneAndTheBell_OfAPoliceNotificationAlert_TellAManagerInChargeAndALicensedCollaboratorWhoWasGivenTheProperty()
    {
        await SeedAsync();
        var police = PushAudience.PropertyHosts(_propertyId, ComplianceAlertAudience.AlloggiatiSubmit);

        await NameAsync("manager");
        Assert.Equal(Of("admin|manager|owner"), await ToldByTheBellAsync(police, "manager-bell"));
        Assert.Equal(Of("admin|manager|owner"), await ToldByThePhoneAsync(police, "manager-phone"));

        await NameAsync("collab-licensed");
        Assert.Equal(Of("admin|collab-licensed|owner"), await ToldByTheBellAsync(police, "licensed-bell"));
        Assert.Equal(Of("admin|collab-licensed|owner"), await ToldByThePhoneAsync(police, "licensed-phone"));

        // The licensed collaborator who lost the property (a stale name) and the deactivated one are never told, license or not.
        await NameAsync("collab-licensed-other");
        Assert.Equal(Of("admin|owner"), await ToldByTheBellAsync(police, "stale-bell"));
        await NameAsync("collab-deactivated");
        Assert.Equal(Of("admin|owner"), await ToldByThePhoneAsync(police, "deactivated-phone"));
    }

    [Fact]
    public async Task APoliceNotificationAlert_WithoutAnEvaluationOfPermissions_TellsNobody_AndNeverTakesAPermissionAsHeld()
    {
        await SeedAsync();
        await NameAsync("collab");
        var police = PushAudience.PropertyHosts(_propertyId, ComplianceAlertAudience.AlloggiatiSubmit);

        // A job built without the evaluation (it is registered in the application, a test could build it without): the narrowed audience fails closed, the plain one is unaffected.
        Assert.Empty(await ToldByTheBellAsync(police, "closed-bell", withTheEvaluation: false));
        Assert.Empty(await ToldByThePhoneAsync(police, "closed-phone", withTheEvaluation: false));
        Assert.NotEmpty(await ToldByTheBellAsync(PushAudience.PropertyHosts(_propertyId), "open-bell", withTheEvaluation: false));
        Assert.NotEmpty(await ToldByThePhoneAsync(PushAudience.PropertyHosts(_propertyId), "open-phone", withTheEvaluation: false));
    }

    [Fact]
    public async Task APoliceNotificationAlert_WhenNobodyWhoReachesThePropertyHoldsTheLicense_NobodyIsToldInTheApp()
    {
        await SeedAsync();
        await NameAsync("collab");

        // Every person with the permission is gone: the owner and the administrators were deactivated. Only a collaborator without the license is left in charge.
        foreach (var member in await _db.OrgMembers.Where(m => m.Role == OrgRole.Owner || m.Role == OrgRole.Admin).ToListAsync())
            member.Status = OrgMemberStatus.Deactivated;
        await _db.SaveChangesAsync();
        var police = PushAudience.PropertyHosts(_propertyId, ComplianceAlertAudience.AlloggiatiSubmit);

        var audience = await Resolver().ForPropertyAsync(_orgId, _propertyId, ComplianceAlertAudience.AlloggiatiSubmit, CancellationToken.None);

        Assert.Empty(audience.UserIds);
        Assert.Equal(OrgAddress, Assert.Single(audience.Emails));
        Assert.Empty(await ToldByTheBellAsync(police, "nobody-bell"));
        Assert.Empty(await ToldByThePhoneAsync(police, "nobody-phone"));
    }

    // ─── The permission rides the queue ───

    [Fact]
    public void ThePermissionOfTheAudience_TravelsInTheArgumentsOfTheTwoJobs_AndAJobQueuedBeforeItExistedNarrowsNobody()
    {
        var property = Guid.NewGuid();
        var narrowed = PushAudience.PropertyHosts(property, ComplianceAlertAudience.AlloggiatiSubmit);
        var plain = PushAudience.PropertyHosts(property);

        Assert.Equal(narrowed, QueuedPush.From(narrowed, Alert()).ToAudience());
        Assert.Equal(narrowed, QueuedInAppNotification.From(narrowed, Alert(), Now).ToAudience());
        Assert.Equal(plain, QueuedPush.From(plain, Alert()).ToAudience());
        Assert.Equal(plain, QueuedInAppNotification.From(plain, Alert(), Now).ToAudience());
        Assert.NotEqual(narrowed, plain);

        // A job queued by the previous version has no permission in its arguments: it is the plain audience, as it was.
        var old = new QueuedPush { AudienceKind = PushAudienceKind.PropertyHosts, AudienceId = property };
        Assert.Null(old.ToAudience().RequiredPermission);
        Assert.Null(new QueuedInAppNotification { AudienceKind = PushAudienceKind.PropertyHosts, AudienceId = property }.ToAudience().RequiredPermission);
    }
}
