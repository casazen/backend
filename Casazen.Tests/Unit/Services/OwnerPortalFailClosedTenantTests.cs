using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F07/US01 (NF-111, ADR-011-R2): the person of the owner portal has no org of its own, so the tenant filter of its request is fail-closed
/// (<c>FilterEnabled</c> true, <c>OrgId</c> null: every <c>ITenantOwned</c> row is hidden). Every read of the portal must ignore it and name the org, the owner
/// profile and the property explicitly. The other proofs build their contexts with no tenant at all, where the filter is off and a read that forgot to ignore it
/// still answers: these run the reads of the portal under the filter an owner really has, and prove that what an owner reaches is there, and only that.
/// </summary>
public class OwnerPortalFailClosedTenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static readonly OwnerContactIndex ContactIndex = new(
        Options.Create(new OwnerPortalOptions()),
        Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Development"),
        NullLogger<OwnerContactIndex>.Instance);

    private readonly string _name = $"owner-portal-fail-closed-{Guid.NewGuid():N}";
    private readonly FixedTimeProvider _clock = new(Now);

    private Guid _orgId;
    private Guid _propertyA;
    private Guid _propertyB;
    private Guid _ownerA;
    private Guid _ownerB;
    private Guid _userA;
    private Guid _userB;
    private Guid _userC;
    private Guid _statementA;
    private Guid _stayA;
    private Guid _conversationA;

    /// <summary>What the request of an owner of the portal has: a filter that is on and an org that is none.</summary>
    private sealed class FailClosedTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => true;
    }

    private DbContextOptions<AppDbContext> DbOptions => new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options;

    private AppDbContext Plain() => new(DbOptions);

    private AppDbContext Portal() => new(DbOptions, new FailClosedTenant(), null);

    private async Task SeedAsync()
    {
        await using var db = Plain();
        var org = HostScopeScenario.NewOrg("Agenzia Mare", $"mare-{Guid.NewGuid():N}"[..20]);
        db.Orgs.Add(org);
        var propertyA = HostScopeScenario.NewProperty(org.Id, "auth0|agenzia", "Casa Alfa");
        var propertyB = HostScopeScenario.NewProperty(org.Id, "auth0|agenzia", "Casa Beta");
        db.Properties.AddRange(propertyA, propertyB);

        var userA = new OwnerUser { Auth0Sub = "auth0|anna", Email = "anna@example.com", FirstName = "Anna", LastName = "Alfa", CreatedAt = Now.UtcDateTime };
        var userB = new OwnerUser { Auth0Sub = "auth0|bruno", Email = "bruno@example.com", FirstName = "Bruno", LastName = "Beta", CreatedAt = Now.UtcDateTime };
        var userC = new OwnerUser { Auth0Sub = "auth0|carla", Email = "carla@example.com", FirstName = "Carla", LastName = "Contatto", CreatedAt = Now.UtcDateTime };
        db.OwnerUsers.AddRange(userA, userB, userC);
        var owners = new List<PropertyOwner>();
        foreach (var (user, property) in new[] { (userA, propertyA), (userB, propertyB) })
        {
            var owner = new PropertyOwner { OrgId = org.Id, FirstName = user.FirstName, LastName = user.LastName, Email = user.Email, CreatedAt = Now.UtcDateTime };
            owners.Add(owner);
            db.PropertyOwners.Add(owner);
            db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = org.Id,
                PropertyId = property.Id,
                PropertyOwnerId = owner.Id,
                OwnerUserId = user.Id,
                SharePercent = 100m,
                OwnershipStart = Now.UtcDateTime.AddYears(-1),
            });
        }

        // A guest stay on each property.
        var guest = new Guest { OrgId = org.Id, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com", PhoneNumber = "+39 333 1234567" };
        db.Guests.Add(guest);
        foreach (var property in new[] { propertyA, propertyB })
        {
            db.Bookings.Add(new Booking
            {
                OrgId = org.Id,
                PropertyId = property.Id,
                GuestId = guest.Id,
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Airbnb,
                TotalPrice = 300m,
                CheckInDate = Today.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CheckOutDate = Today.AddDays(8).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
        }

        // A published statement of each owner profile, with the link of the invoice an external system issued for it.
        var statementIds = new List<Guid>();
        foreach (var owner in owners)
        {
            var statement = new OwnerStatementVersion
            {
                OrgId = org.Id,
                PropertyOwnerId = owner.Id,
                PeriodYear = 2026,
                PeriodMonth = 9,
                VersionNumber = 1,
                Status = OwnerStatementVersionStatus.Published,
                Currency = "EUR",
                CutoffAt = Now.UtcDateTime,
                EntriesSha256 = new string('a', 64),
                PublishedAt = Now.UtcDateTime,
                PublishedSha256 = new string('b', 64),
                CreatedAt = Now.UtcDateTime,
            };
            statementIds.Add(statement.Id);
            db.OwnerStatementVersions.Add(statement);
            db.OwnerExternalDocumentReferences.Add(new OwnerExternalDocumentReference
            {
                OrgId = org.Id,
                OwnerStatementVersionId = statement.Id,
                PropertyOwnerId = owner.Id,
                Provider = "fatture",
                ExternalId = $"ext-{owner.Id:N}",
                ExternalNumber = "12/2026",
                ExternalDate = Today,
                CreatedAt = Now.UtcDateTime,
            });

            // A document shared with the owner profile, a conversation with one message of the agency, and a stay asked on its property.
            var document = new OwnerDocument
            {
                OrgId = org.Id,
                PropertyOwnerId = owner.Id,
                Type = OwnerDocumentType.Mandate,
                Title = "Mandato",
                CreatedAt = Now.UtcDateTime,
                CreatedByUserId = "auth0|agenzia",
                UpdatedAt = Now.UtcDateTime,
            };
            db.OwnerDocuments.Add(document);
            db.OwnerDocumentVersions.Add(new OwnerDocumentVersion
            {
                OrgId = org.Id,
                OwnerDocumentId = document.Id,
                VersionNumber = 1,
                StorageKey = $"owner-documents/{document.Id:N}/1.pdf",
                FileName = "mandato.pdf",
                ContentType = OwnerFileRules.Pdf,
                SizeBytes = 1,
                Sha256 = new string('c', 64),
                ScanEngine = "test",
                UploadedAt = Now.UtcDateTime,
                UploadedByUserId = "auth0|agenzia",
            });
            var conversation = new OwnerConversation
            {
                OrgId = org.Id,
                PropertyOwnerId = owner.Id,
                Topic = OwnerConversationTopic.General,
                Subject = "Benvenuto",
                CreatedAt = Now.UtcDateTime,
                LastMessageAt = Now.UtcDateTime,
            };
            db.OwnerConversations.Add(conversation);
            db.OwnerMessages.Add(new OwnerMessage
            {
                OrgId = org.Id,
                ConversationId = conversation.Id,
                Sender = OwnerMessageSender.Agency,
                SenderUserId = "auth0|agenzia",
                Body = "Buongiorno",
                CreatedAt = Now.UtcDateTime,
            });
            var stay = new OwnerStayRequest
            {
                OrgId = org.Id,
                PropertyId = owner == owners[0] ? propertyA.Id : propertyB.Id,
                PropertyOwnerId = owner.Id,
                RequestedByOwnerUserId = owner == owners[0] ? userA.Id : userB.Id,
                StartDate = Today.AddDays(20),
                EndDate = Today.AddDays(22),
                Nights = 2,
                Status = OwnerStayStatus.Requested,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            };
            db.OwnerStayRequests.Add(stay);
            if (owner == owners[0])
            {
                (_conversationA, _stayA) = (conversation.Id, stay.Id);
            }
        }

        // A contact the first owner lets into its portal with the inbox and the documents, with the account of a third person.
        db.OwnerPortalContacts.Add(new OwnerPortalContact
        {
            OrgId = org.Id,
            PropertyOwnerId = owners[0].Id,
            Email = "carla@example.com",
            EmailHash = ContactIndex.HashEmail("carla@example.com"),
            FirstName = "Carla",
            LastName = "Contatto",
            Permissions = OwnerPortalPermission.InboxUse | OwnerPortalPermission.DocumentsRead,
            Status = OwnerPortalContactStatus.Active,
            TokenHash = new string('d', 64),
            InvitationExpiresAt = Now.UtcDateTime.AddDays(7),
            InvitedByOwnerUserId = userA.Id,
            OwnerUserId = userC.Id,
            AcceptedAt = Now.UtcDateTime,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
        });

        await db.SaveChangesAsync();
        (_orgId, _propertyA, _propertyB, _ownerA, _ownerB, _userA, _userB, _userC, _statementA) =
            (org.Id, propertyA.Id, propertyB.Id, owners[0].Id, owners[1].Id, userA.Id, userB.Id, userC.Id, statementIds[0]);
    }

    private OwnerStayService Stays(AppDbContext db) =>
        new(
            db,
            Mock.Of<ICalendarBlockService>(),
            Mock.Of<ICalendarChangeRunner>(),
            Mock.Of<IInternalActivityService>(),
            new ActivityLog(db, _clock),
            _clock,
            NullLogger<OwnerStayService>.Instance,
            Mock.Of<IBackgroundJobClient>());

    private OwnerCollaborationService Collaboration(AppDbContext db) =>
        new(
            db,
            Mock.Of<IFileStorage>(),
            new OwnerFileContentScanner(),
            new OwnerPortalAccessResolver(db, _clock),
            new ActivityLog(db, _clock),
            Mock.Of<IInternalActivityService>(),
            Mock.Of<IEmailQueue>(),
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://casazen.example.test/" })),
            Mock.Of<IAuth0ManagementService>(),
            ContactIndex,
            _clock,
            NullLogger<OwnerCollaborationService>.Instance);

    private Task<OwnerPortalAccess> AccessAsync(Guid ownerUserId) => new OwnerPortalAccessResolver(Portal(), _clock).ResolveAsync(ownerUserId);

    [Fact]
    public async Task TheFilterOfTheRequestOfAnOwner_HidesEveryTenantRow_ThisIsWhatTheOtherReadsMustIgnore()
    {
        await SeedAsync();

        await using var portal = Portal();
        await using var plain = Plain();

        Assert.Equal(0, await portal.PropertyOwnerships.CountAsync());
        Assert.Equal(0, await portal.Properties.CountAsync());
        Assert.Equal(0, await portal.Bookings.CountAsync());
        Assert.Equal(2, await plain.PropertyOwnerships.CountAsync());
    }

    [Fact]
    public async Task OwnerPortal_TheAccessOfAnOwnerAndOfAContact_IsResolvedUnderTheFailClosedFilter()
    {
        await SeedAsync();

        var owner = await AccessAsync(_userA);
        Assert.Equal([(_orgId, _ownerA, _propertyA, (Guid?)null)], owner.Grants.Select(g => (g.OrgId, g.PropertyOwnerId, g.PropertyId, g.ContactId)));
        Assert.Equal(OwnerPortalPermissions.Everything, Assert.Single(owner.Grants).Permissions);

        // The contact holds what the owner gave it, on the properties of that owner profile: nothing of the other owner.
        var contact = await AccessAsync(_userC);
        var grant = Assert.Single(contact.Grants);
        Assert.Equal((_orgId, _ownerA, _propertyA), (grant.OrgId, grant.PropertyOwnerId, grant.PropertyId));
        Assert.Equal(OwnerPortalPermission.InboxUse | OwnerPortalPermission.DocumentsRead, grant.Permissions);
    }

    [Fact]
    public async Task OwnerStays_TheReadsOfTheOwner_AnswerUnderTheFailClosedFilter_AndOnlyWhatTheOwnerReaches()
    {
        await SeedAsync();
        var access = await AccessAsync(_userA);
        await using var db = Portal();
        var service = Stays(db);

        // The calendar sees the guest stay as a night taken, never who; the stays and the rule are the owner's own.
        var calendar = await service.GetCalendarAsync(access, _propertyA, Today, Today.AddDays(30));
        Assert.Contains(calendar.Ranges, r => r.Kind == OwnerCalendarKinds.Occupied && r.Start == Today.AddDays(5) && r.End == Today.AddDays(8));
        Assert.Contains(calendar.Ranges, r => r.Kind == OwnerCalendarKinds.OwnerStay && r.StayId == _stayA);
        Assert.Equal([_stayA], (await service.ListForOwnerAsync(access)).Select(s => s.Id));
        Assert.Equal(_propertyA, (await service.GetRuleForOwnerAsync(access, _propertyA)).Rule.PropertyId);

        // Another owner's property is a 404 here as everywhere else.
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(() => service.GetCalendarAsync(access, _propertyB, Today, Today.AddDays(30)));
    }

    [Fact]
    public async Task OwnerCollaboration_TheReadsOfTheOwnerAndOfItsContact_AnswerUnderTheFailClosedFilter_AndOnlyWhatTheyReach()
    {
        await SeedAsync();
        var owner = await AccessAsync(_userA);
        var contact = await AccessAsync(_userC);
        await using var db = Portal();
        var service = Collaboration(db);

        // The owner: its document, its published statement, its conversation and the contact it let in.
        var documents = await service.ListDocumentsForOwnerAsync(owner);
        Assert.Equal(_ownerA, Assert.Single(documents.Documents).PropertyOwnerId);
        Assert.Equal(_statementA, Assert.Single(documents.Statements).StatementVersionId);
        Assert.Equal(_conversationA, Assert.Single(await service.ListConversationsForOwnerAsync(owner)).Id);
        Assert.Equal("Buongiorno", Assert.Single(await service.ListMessagesForOwnerAsync(owner, _conversationA)).Body);
        Assert.Equal("carla@example.com", Assert.Single(await service.ListContactsForOwnerAsync(owner)).Email);

        // The contact: the documents and the inbox the owner gave it, not the statements (no accounting.read), not the management of contacts.
        var forContact = await service.ListDocumentsForOwnerAsync(contact);
        Assert.Single(forContact.Documents);
        Assert.Empty(forContact.Statements);
        Assert.Equal(_conversationA, Assert.Single(await service.ListConversationsForOwnerAsync(contact)).Id);
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.DomainForbiddenException>(() => service.ListContactsForOwnerAsync(contact));
    }

    [Fact]
    public async Task OwnerPortalService_TheExistingReads_AnswerUnderTheFailClosedFilter_AndOnlyWhatTheOwnerReaches()
    {
        await SeedAsync();
        await using var db = Portal();
        var portal = new OwnerPortalService(db, _clock);

        // The properties of the owner with their bookings, and nobody else's.
        var properties = await portal.GetOwnerPropertiesAsync(_userA);
        var property = Assert.Single(properties);
        Assert.Equal((_propertyA, 1, 300m), (property.PropertyId, property.TotalBookings, property.TotalRevenue));
        Assert.Equal(_propertyB, Assert.Single(await portal.GetOwnerPropertiesAsync(_userB)).PropertyId);
        Assert.True(await portal.OwnerHasAccessToPropertyAsync(_userA, _propertyA));
        Assert.False(await portal.OwnerHasAccessToPropertyAsync(_userA, _propertyB));

        // The links of the invoices an external system issued for the published statements of the owner: its own, not the other owner's.
        var accounting = new OwnerAccountingService(db, Mock.Of<IOwnerFiscalProfileService>(), _clock, NullLogger<OwnerAccountingService>.Instance);
        var documents = await accounting.ListPublishedExternalDocumentsForOwnerUserAsync(_userA);
        Assert.Equal(_statementA, Assert.Single(documents).OwnerStatementVersionId);
        Assert.Equal(_ownerA, documents.Single().PropertyOwnerId);
    }
}
