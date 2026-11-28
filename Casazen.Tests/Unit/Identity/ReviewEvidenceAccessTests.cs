using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US02/T02 (NF-195, decision D9): the evidence kept for the reviewer is served only to a caller with <c>verification.decision</c> on a property it reaches of its own
/// organization; every other case (another org, another property, expired, deleted, not of the booking) is the same 404 <c>identity_evidence_not_found</c>. Each picture served
/// writes first one line <c>IdentityEvidenceViewed</c> in the activity log, without the picture; the response is never cached, never sniffed and inline; and the decision of an
/// operator deletes the evidence of the booking (reason <c>decided</c>).
/// </summary>
public sealed class ReviewEvidenceAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ReviewEvidenceAccess_Owner_ListsAndReadsThePicture()
    {
        await using var world = await AccessWorld.CreateAsync();

        var items = await world.Store().ListAsync(world.OwnerScope, world.BookingOfP2, Ct);

        Assert.NotNull(items);
        Assert.Equal(world.EvidenceOfP2.Count, items.Count);
        Assert.Equal(IdentityEvidenceKind.DocumentPortrait, items[0].Kind);
        var frame = world.EvidenceOfP2.First(row => row.Kind == IdentityEvidenceKind.Frame);
        var content = await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, frame.Id, AccessWorld.Owner, Ct);

        Assert.NotNull(content);
        Assert.Equal((IdentityEvidenceKind.Frame, IdentityReviewEvidenceStore.JpegContentType), (content.Kind, content.ContentType));
        Assert.Equal(world.PlainOf(frame), content.Bytes);
    }

    [Fact]
    public async Task ReviewEvidenceAccess_OtherPropertyManager_Gets404()
    {
        // Proof (permission, AC1): a property manager «Solo alcuni» who reaches P1 only gets nothing of a booking of P2, the list or a picture (the same answer as for nothing at
        // all), and no line is written; the evidence of the booking of P1 it reaches is served (the positive control).
        await using var world = await AccessWorld.CreateAsync();
        var picture = world.EvidenceOfP2[0];

        Assert.Null(await world.Store().ListAsync(world.ManagerOfP1Scope, world.BookingOfP2, Ct));
        Assert.Null(await world.Store().ReadAsync(world.ManagerOfP1Scope, world.BookingOfP2, picture.Id, AccessWorld.Manager, Ct));
        Assert.Empty(await world.ActivityAsync());

        var reachable = Assert.Single((await world.Store().ListAsync(world.ManagerOfP1Scope, world.BookingOfP1, Ct))!);
        Assert.NotNull(await world.Store().ReadAsync(world.ManagerOfP1Scope, world.BookingOfP1, reachable.Id, AccessWorld.Manager, Ct));
    }

    [Fact]
    public async Task ReviewEvidenceAccess_OtherOrg_Gets404()
    {
        // Proof (permission, AC1): the owner of another organization gets nothing of a booking of this one, nor through a booking of its own named with an evidence of this org.
        await using var world = await AccessWorld.CreateAsync();
        var picture = world.EvidenceOfP2[0];

        Assert.Null(await world.Store().ListAsync(world.OtherOrgScope, world.BookingOfP2, Ct));
        Assert.Null(await world.Store().ReadAsync(world.OtherOrgScope, world.BookingOfP2, picture.Id, AccessWorld.OtherOwner, Ct));
        Assert.Null(await world.Store().ReadAsync(world.OtherOrgScope, world.BookingOfOtherOrg, picture.Id, AccessWorld.OtherOwner, Ct));
        Assert.Null(await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP1, picture.Id, AccessWorld.Owner, Ct));
        Assert.Empty(await world.ActivityAsync());
    }

    [Fact]
    public async Task ReviewEvidenceAccess_ExpiredOrDeleted_Gets404()
    {
        await using var world = await AccessWorld.CreateAsync();
        var deleted = world.EvidenceOfP2[1];
        await using (var db = world.NewContext())
        {
            var row = await db.IdentityReviewEvidences.IgnoreQueryFilters().SingleAsync(evidence => evidence.Id == deleted.Id);
            row.DeletedAt = Now.UtcDateTime;
            row.DeletionReason = IdentityEvidenceDeletionReasons.Decided;
            await db.SaveChangesAsync();
        }

        Assert.Null(await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, deleted.Id, AccessWorld.Owner, Ct));
        Assert.DoesNotContain((await world.Store().ListAsync(world.OwnerScope, world.BookingOfP2, Ct))!, item => item.Id == deleted.Id);

        // Past its expiry (48 hours by default) nothing is served, even before the purge deletes it; the booking is still reachable: its list is empty.
        world.Clock.Advance(TimeSpan.FromHours(48));
        Assert.Null(await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, world.EvidenceOfP2[0].Id, AccessWorld.Owner, Ct));
        Assert.Empty((await world.Store().ListAsync(world.OwnerScope, world.BookingOfP2, Ct))!);
        Assert.Empty(await world.ActivityAsync());
    }

    [Fact]
    public async Task ReviewEvidenceAccess_Read_WritesAuditEntry()
    {
        // Proof (privacy, AC2): every picture served writes one line of the activity log of the org: the reader, the booking, the kind of the picture, and nothing of the picture.
        await using var world = await AccessWorld.CreateAsync();
        var crop = world.EvidenceOfP2.Single(row => row.Kind == IdentityEvidenceKind.DocumentPortrait);
        var frame = world.EvidenceOfP2.First(row => row.Kind == IdentityEvidenceKind.Frame);

        await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, crop.Id, AccessWorld.Owner, Ct);
        await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, frame.Id, AccessWorld.Owner, Ct);

        var lines = await world.ActivityAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(
                (world.OrgId, OrgActivityType.IdentityEvidenceViewed, AccessWorld.Owner, world.BookingOfP2.ToString(), OrgActivitySubjectType.Booking, OrgActivityArea.ShortRent),
                (line.OrgId, line.Type, line.ActorUserId, line.SubjectId, line.SubjectType, line.Area));
            Assert.DoesNotContain(Convert.ToBase64String(world.PlainOf(frame)), line.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("identity-evidence", line.DetailsJson, StringComparison.Ordinal);
        });
        Assert.Equal(
            ["{\"part\":\"documentPortrait\"}", "{\"part\":\"frame\"}"],
            lines.Select(line => line.DetailsJson).Order(StringComparer.Ordinal));

        // No activity log, no picture: the read fails closed.
        Assert.Null(await world.Store(withActivityLog: false).ReadAsync(world.OwnerScope, world.BookingOfP2, frame.Id, AccessWorld.Owner, Ct));
        Assert.Equal(2, (await world.ActivityAsync()).Count);
    }

    [Fact]
    public async Task ReviewEvidenceAccess_ATamperedObject_IsNotServed()
    {
        await using var world = await AccessWorld.CreateAsync();
        var frame = world.EvidenceOfP2.First(row => row.Kind == IdentityEvidenceKind.Frame);
        world.Storage.Objects[frame.StorageKey][^1] ^= 0xFF;

        Assert.Null(await world.Store().ReadAsync(world.OwnerScope, world.BookingOfP2, frame.Id, AccessWorld.Owner, Ct));
        Assert.Empty(await world.ActivityAsync());
    }

    [Fact]
    public async Task ReviewEvidenceAccess_Controller_ServesThePictureNeverCached_AndAnswersTheSame404()
    {
        // AC3: the picture is sent with no-store, private, no-cache, nosniff and inline, as its content type; every case where it is not served is 404 identity_evidence_not_found.
        await using var world = await AccessWorld.CreateAsync();
        var frame = world.EvidenceOfP2.First(row => row.Kind == IdentityEvidenceKind.Frame);
        var (controller, response) = world.Controller(AccessWorld.Owner, world.OwnerScope);

        var served = await controller.GetEvidenceContent(world.BookingOfP2, frame.Id, world.Store(), Ct);

        Assert.IsType<EmptyResult>(served);
        Assert.Equal("no-store, private", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", controller.Response.Headers.Pragma.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("inline", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(IdentityReviewEvidenceStore.JpegContentType, controller.Response.ContentType);
        Assert.Equal(world.PlainOf(frame), response.ToArray());

        var list = Assert.IsType<OkObjectResult>((await controller.GetEvidence(world.BookingOfP2, world.Store(), Ct)).Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<IdentityEvidenceItemDto>>(list.Value);
        Assert.Equal(["documentPortrait", "frame", "frame", "frame"], dtos.Select(dto => dto.Kind));
        Assert.All(dtos, dto => Assert.Equal(Now.UtcDateTime.AddHours(48), dto.ExpiresAt));

        var (outsider, _) = world.Controller(AccessWorld.Manager, world.ManagerOfP1Scope);
        AssertNotFound(await outsider.GetEvidenceContent(world.BookingOfP2, frame.Id, world.Store(), Ct));
        AssertNotFound((await outsider.GetEvidence(world.BookingOfP2, world.Store(), Ct)).Result);
        AssertNotFound(await controller.GetEvidenceContent(world.BookingOfP2, Guid.NewGuid(), world.Store(), Ct));
    }

    [Fact]
    public void ReviewEvidenceAccess_TheEndpoints_AreUnderTheOperatorPolicy_AndBehindTheThreeFlags()
    {
        Assert.Equal([CasazenPolicies.VerificationDecision], typeof(VerificationController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        foreach (var (name, template) in new[]
                 {
                     (nameof(VerificationController.GetEvidence), "{bookingId:guid}/evidence"),
                     (nameof(VerificationController.GetEvidenceContent), "{bookingId:guid}/evidence/{evidenceId:guid}"),
                 })
        {
            var method = typeof(VerificationController).GetMethod(name)!;
            Assert.Equal(template, method.GetCustomAttribute<HttpGetAttribute>()!.Template);
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Equal(
                new[] { FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityReviewEvidence }.Order(),
                method.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(gate => gate.Flag).Order());
        }
    }

    [Fact]
    public void ReviewEvidenceAccess_TheTitleOfThe404_IsInItalianAndInEnglish()
    {
        var italian = Resources(english: false);
        var english = Resources(english: true);

        Assert.True(italian.TryGetValue("IdentityEvidenceNotFound", out var it) && !string.IsNullOrWhiteSpace(it));
        Assert.True(english.TryGetValue("IdentityEvidenceNotFound", out var en) && !string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(it, en);
        Assert.Equal("identity_evidence_not_found", IdentityApiErrors.IdentityEvidenceNotFound);
    }

    [Fact]
    public async Task ReviewEvidenceAccess_DecisionDeletesEvidence()
    {
        // Proof (data, AC4): the decision of an operator, an approval or a rejection, deletes the evidence of the booking: the objects leave the bucket and the rows stay as the
        // trace, marked deleted with the reason decided. The evidence of another booking stays.
        await using var world = await AccessWorld.CreateAsync();
        await world.HandOffAsync(world.BookingOfP2);
        await world.HandOffAsync(world.BookingOfP1);

        Assert.True(await world.Queue().RecordManualVerificationAsync(world.BookingOfP2, world.OwnerScope, true, null, AccessWorld.Owner, Ct));

        var rows = await world.RowsAsync();
        Assert.All(rows.Where(row => row.BookingId == world.BookingOfP2), row =>
        {
            Assert.Equal((Now.UtcDateTime, IdentityEvidenceDeletionReasons.Decided), (row.DeletedAt, row.DeletionReason));
            Assert.False(world.Storage.Objects.ContainsKey(row.StorageKey));
        });
        var other = Assert.Single(rows, row => row.BookingId == world.BookingOfP1);
        Assert.Null(other.DeletedAt);
        Assert.True(world.Storage.Objects.ContainsKey(other.StorageKey));

        Assert.True(await world.Queue().RejectVerificationAsync(world.BookingOfP1, world.OwnerScope, "documento non leggibile", AccessWorld.Owner, Ct));

        var rejected = Assert.Single(await world.RowsAsync(), row => row.BookingId == world.BookingOfP1);
        Assert.Equal(IdentityEvidenceDeletionReasons.Decided, rejected.DeletionReason);
        Assert.Empty(world.Storage.Objects);
    }

    [Fact]
    public async Task ReviewEvidenceAccess_AFailedDeletion_DoesNotUndoTheDecision()
    {
        await using var world = await AccessWorld.CreateAsync();
        await world.HandOffAsync(world.BookingOfP2);
        world.Storage.FailDeletes = new IOException("The bucket is down.");

        Assert.True(await world.Queue().RecordManualVerificationAsync(world.BookingOfP2, world.OwnerScope, true, null, AccessWorld.Owner, Ct));

        await using var read = world.NewContext();
        Assert.Contains(
            await read.BiometricVerificationLogs.IgnoreQueryFilters().Where(log => log.BookingId == world.BookingOfP2).ToListAsync(),
            log => log.Method == VerificationMethod.Operator && log.Outcome == BiometricOutcome.Accept);
        Assert.All((await world.RowsAsync()).Where(row => row.BookingId == world.BookingOfP2), row => Assert.Null(row.DeletedAt));
    }

    private static void AssertNotFound(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IdentityApiErrors.IdentityEvidenceNotFound, Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static Dictionary<string, string> Resources(bool english)
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        var path = Path.Combine(root, "Casazen.Web", "Resources", english ? "SharedResources.en.resx" : "SharedResources.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>
    /// One org with two properties of its owner (P1 and P2), a booking on each, a property manager «Solo alcuni» who reaches P1 only, another org with its booking; the evidence
    /// of the booking of P2 (a crop and three frames) and of the booking of P1 (one frame), kept by the store as the closure keeps it.
    /// </summary>
    private sealed class AccessWorld : IAsyncDisposable
    {
        public const string Owner = "auth0|owner-e73";
        public const string Manager = "auth0|manager-e73";
        public const string OtherOwner = "auth0|other-e73";

        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"evidence-access-{Guid.NewGuid()}").Options;

        private readonly Dictionary<Guid, byte[]> _plain = [];

        private AccessWorld()
        {
            Db = new AppDbContext(_options);
            Detector.Fallback = _ => [new DetectedFace(60, 20, 40, 50, 0.99f, new float[10], 0.08)];
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid OtherOrgId { get; } = Guid.NewGuid();

        public Guid PropertyP1 { get; } = Guid.NewGuid();

        public Guid PropertyP2 { get; } = Guid.NewGuid();

        public Guid BookingOfP1 { get; } = Guid.NewGuid();

        public Guid BookingOfP2 { get; } = Guid.NewGuid();

        public Guid BookingOfOtherOrg { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public FailingMemoryStorage Storage { get; } = new();

        public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

        public FakeTimeProvider Clock { get; } = new(Now);

        public TestFeatureFlags Flags { get; } =
            new(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityReviewEvidence);

        public KeyedFaceDetector Detector { get; } = new();

        public HostScope OwnerScope => new(OrgId);

        public HostScope ManagerOfP1Scope => new(OrgId, GrantedToUserId: Manager);

        public HostScope OtherOrgScope => new(OtherOrgId);

        public List<IdentityReviewEvidence> EvidenceOfP2 { get; private set; } = [];

        public static async Task<AccessWorld> CreateAsync()
        {
            var world = new AccessWorld();
            var db = world.Db;
            db.Properties.AddRange(Property(world.PropertyP1, world.OrgId, Owner), Property(world.PropertyP2, world.OrgId, Owner), Property(Guid.NewGuid(), world.OtherOrgId, OtherOwner));
            db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = world.OrgId, UserId = Manager, PropertyId = world.PropertyP1 });
            var guest = new Guest { Id = Guid.NewGuid(), OrgId = world.OrgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
            var otherGuest = new Guest { Id = Guid.NewGuid(), OrgId = world.OtherOrgId, FirstName = "Ugo", LastName = "Neri", Email = "ugo@example.com" };
            db.Guests.AddRange(guest, otherGuest);
            db.Bookings.AddRange(
                Booking(world.BookingOfP1, world.OrgId, world.PropertyP1, guest.Id),
                Booking(world.BookingOfP2, world.OrgId, world.PropertyP2, guest.Id),
                Booking(world.BookingOfOtherOrg, world.OtherOrgId, db.Properties.Local.Single(p => p.OrgId == world.OtherOrgId).Id, otherGuest.Id));
            await db.SaveChangesAsync();

            var (document, pixels) = IdentityReviewEvidenceStoreTests.Document(200, 120, seed: 7);
            pixels.Dispose();
            using (var sequence = IdentityReviewEvidenceStoreTests.Sequence(4, withMetadata: true))
            {
                await world.Store().SaveForReviewAsync(
                    new ReviewEvidenceInput(world.OrgId, world.BookingOfP2, Guid.NewGuid(), guest.Id, Guid.NewGuid(), false, document, sequence, [0, 1, 2]), Ct);
                await world.Store().SaveForReviewAsync(
                    new ReviewEvidenceInput(world.OrgId, world.BookingOfP1, Guid.NewGuid(), guest.Id, Guid.NewGuid(), false, null, sequence, [3]), Ct);
            }

            world.EvidenceOfP2 = [.. (await world.RowsAsync()).Where(row => row.BookingId == world.BookingOfP2).OrderBy(row => row.Kind)];
            foreach (var row in await world.RowsAsync())
                world._plain[row.Id] = world.DataProtection.CreateProtector(IdentityReviewEvidenceStore.ContentPurpose, row.Id.ToString("N")).Unprotect(world.Storage.Objects[row.StorageKey]);

            db.ChangeTracker.Clear();
            return world;
        }

        public IdentityReviewEvidenceStore Store(bool withActivityLog = true) =>
            new(
                Db,
                Storage,
                DataProtection,
                Flags,
                Options.Create(new IdentityVerificationOptions()),
                Options.Create(new BiometricModelOptions()),
                Detector,
                Clock,
                NullLogger<IdentityReviewEvidenceStore>.Instance,
                withActivityLog ? new ActivityLog(Db, Clock) : null);

        public VerificationQueueService Queue() =>
            new(
                Db,
                Mock.Of<IEmailQueue>(),
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
                Mock.Of<IGuestCheckInService>(),
                NullLogger<VerificationQueueService>.Instance,
                timeProvider: Clock,
                reviewEvidence: Store());

        public (VerificationController Controller, MemoryStream Body) Controller(string userId, HostScope scope)
        {
            var scopes = new Mock<IHostScopeResolver>();
            scopes.Setup(s => s.ResolveAsync(userId, It.IsAny<IReadOnlySet<string>>(), scope.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
            var orgs = new Mock<IOrgContextResolver>();
            orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(scope.OrgId);
            var body = new MemoryStream();
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            context.Response.Body = body;
            var controller = new VerificationController(
                Mock.Of<IVerificationQueueService>(), scopes.Object, orgs.Object, NullLogger<VerificationController>.Instance, Clock)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
            return (controller, body);
        }

        /// <summary>The verification of the booking waits for an operator: its session was reviewed and handed over.</summary>
        public async Task HandOffAsync(Guid bookingId)
        {
            Db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                BookingId = bookingId,
                OrgId = OrgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                BiometricOutcome = BiometricOutcome.Escalate,
                BiometricAttempts = 1,
                OperatorRequestedAt = Now.UtcDateTime.AddMinutes(-30),
                ExpiresAt = Now.UtcDateTime.AddDays(7),
                CreatedAt = Now.UtcDateTime.AddHours(-1),
                UpdatedAt = Now.UtcDateTime.AddMinutes(-30),
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public byte[] PlainOf(IdentityReviewEvidence row) => _plain[row.Id];

        public AppDbContext NewContext() => new(_options);

        public async Task<List<IdentityReviewEvidence>> RowsAsync()
        {
            await using var read = NewContext();
            return await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        }

        public async Task<List<OrgActivityEntry>> ActivityAsync()
        {
            await using var read = NewContext();
            return await read.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static Property Property(Guid id, Guid orgId, string ownerId) => new()
        {
            Id = id,
            OrgId = orgId,
            OwnerId = ownerId,
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Country = "IT",
            NightlyRate = 100m,
            MaxGuests = 4,
        };

        private static Booking Booking(Guid id, Guid orgId, Guid propertyId, Guid guestId) => new()
        {
            Id = id,
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
    }
}
