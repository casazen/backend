using System.Text;
using System.Text.Json;
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
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Identity;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E73/F01/US01/T02 (NF-194, AC-NF194-1) on real PostgreSQL, what memory cannot show: two copies of the same attempt (same session, same <c>Idempotency-Key</c>) sent at the same moment,
/// each on its own connection, serialize on the row of the session (<c>SELECT … FOR UPDATE</c>) and decide once: one row of the log, one outcome, the same answer; and the unique index
/// on (booking, key) keeps a second row out whatever the path.
/// </summary>
public sealed class PresenceClosurePostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-presence-postgres-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        if (!PostgresTestServer.IsAvailable)
            return;

        Directory.CreateDirectory(_directory);
        _database = await PostgresTestDatabase.CreateMigratedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [PostgresFact]
    public async Task PresenceClosurePostgres_ConcurrentDuplicates_DecideOnce()
    {
        var (sessionId, bookingId) = await SeedReadyStayAsync();
        var collector = new ScriptedEvidenceCollector();
        var policy = Policy();
        var key = Guid.NewGuid().ToString();

        string token;
        await using (var issuing = NewContext())
        {
            var session = await LoadAsync(issuing, sessionId);
            var issued = await Flow(issuing, collector, policy).IssuePresenceChallengeAsync(session, CancellationToken.None);
            Assert.NotNull(issued);
            token = issued.ChallengeToken;
        }

        await using var leftDb = NewContext();
        await using var rightDb = NewContext();
        var left = await LoadAsync(leftDb, sessionId);
        var right = await LoadAsync(rightDb, sessionId);
        var answers = await Task.WhenAll(
            Task.Run(() => Flow(leftDb, collector, policy).SubmitPresenceSequenceAsync(left, Submission(key, token), CancellationToken.None)),
            Task.Run(() => Flow(rightDb, collector, policy).SubmitPresenceSequenceAsync(right, Submission(key, token), CancellationToken.None)));

        Assert.All(answers, answer => Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Operator), (answer.Status, answer.Result)));
        Assert.Single(answers, answer => answer.Replayed);
        Assert.Equal(1, collector.Calls);

        await using var read = NewContext();
        var rows = await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Where(row => row.BookingId == bookingId).ToListAsync();
        var row = Assert.Single(rows);

        Assert.Equal((key, IdentityDecisionMode.Observation, BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.IdempotencyKey, row.DecisionMode, row.Outcome, row.ShadowOutcome));
        var saved = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal((1, IdentityOutcomeCodes.Review, key), (saved.BiometricAttempts, saved.IdentityOutcomeCode, saved.IdentityLastAttemptKey));

        await using var twin = NewContext();
        twin.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = row.OrgId,
            BookingId = row.BookingId,
            GuestId = row.GuestId,
            Outcome = BiometricOutcome.Accept,
            Attempts = 1,
            ModelName = "SFace",
            ModelVersion = "2021dec",
            IdempotencyKey = key,
            DecisionMode = IdentityDecisionMode.Automatic,
            CreatedAt = Now.UtcDateTime,
        });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => twin.SaveChangesAsync());
        Assert.True(OrgTeamAccess.IsUniqueViolation(refused, BiometricVerificationLog.IdempotencyIndexName));
    }

    /// <summary>
    /// The last line of the idempotent closure on real PostgreSQL (AC3, g): a twin of the attempt, another request in another process, writes the row of the same key after this request read
    /// the replay again under the lock of the session and before its save. The unique index on (booking, key) refuses the row of this request (23505), its one save is rolled back whole, and
    /// it answers the outcome the twin stored, as a replay: no error, one row (the twin's), the session and its challenge as they were.
    /// </summary>
    [PostgresFact]
    public async Task PresenceClosurePostgres_ATwinThatWritesTheRowFirst_GetsTheStoredOutcome()
    {
        var (sessionId, bookingId) = await SeedReadyStayAsync();
        var collector = new ScriptedEvidenceCollector();
        var policy = Policy();
        var key = Guid.NewGuid().ToString();
        string token;
        await using (var issuing = NewContext())
        {
            var session = await LoadAsync(issuing, sessionId);
            var issued = await Flow(issuing, collector, policy).IssuePresenceChallengeAsync(session, CancellationToken.None);
            Assert.NotNull(issued);
            token = issued.ChallengeToken;
        }

        // The twin writes its row when the pipeline of this request is asked: after the replay was read again under the lock, before the save. Its own context and connection, committed
        // at once; the shape and the lengths of the row are the ones the closure writes (no check constraint on the table, the filtered unique index is the only one that can refuse it).
        var twinId = Guid.Empty;
        collector.OnCollect = async () =>
        {
            await using var twin = NewContext();
            var twinSession = await LoadAsync(twin, sessionId);
            var twinRow = new BiometricVerificationLog
            {
                OrgId = twinSession.OrgId,
                BookingId = twinSession.BookingId,
                GuestId = twinSession.Booking.GuestId,
                Outcome = BiometricOutcome.Escalate,
                Attempts = 1,
                ModelName = "SFace",
                ModelVersion = "2021dec",
                PolicyVersion = "identity-policy-2026.11-1",
                PolicyHash = new string('c', 64),
                DecisionMode = IdentityDecisionMode.Observation,
                ShadowOutcome = BiometricOutcome.Escalate,
                FormatId = "it-cie-3.0",
                Channel = IdentityChannels.AndroidChrome,
                IdempotencyKey = key,
                CreatedAt = Now.UtcDateTime,
            };
            twin.BiometricVerificationLogs.Add(twinRow);
            await twin.SaveChangesAsync();
            twinId = twinRow.Id;
        };

        await using var db = NewContext();
        var loaded = await LoadAsync(db, sessionId);
        var answer = await Flow(db, collector, policy).SubmitPresenceSequenceAsync(loaded, Submission(key, token), CancellationToken.None);

        Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Operator, true), (answer.Status, answer.Result, answer.Replayed));
        Assert.NotNull(answer.State);
        Assert.Equal(1, collector.Calls);

        await using var read = NewContext();
        var row = Assert.Single(await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.BookingId == bookingId).ToListAsync());
        Assert.Equal((twinId, key), (row.Id, row.IdempotencyKey));
        var saved = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal((0, (DateTime?)null, (string?)null), (saved.BiometricAttempts, saved.IdentityClosedAt, saved.IdentityLastAttemptKey));
        Assert.NotNull(PresenceClosureKit.ReadCheck(saved).Presence);
    }

    private AppDbContext NewContext() => new(_database!.CreateOptions(), null, _dataProtection);

    private static Task<GuestCheckInSession> LoadAsync(AppDbContext db, Guid sessionId) =>
        db.GuestCheckInSessions
            .IgnoreQueryFilters()
            .Include(s => s.Booking).ThenInclude(b => b.Property)
            .Include(s => s.Booking).ThenInclude(b => b.Guest)
            .SingleAsync(s => s.Id == sessionId);

    private IdentityPolicyProvider Policy()
    {
        const string file = "identity-policy-of-the-test.json";
        File.WriteAllText(Path.Combine(_directory, file), IdentityPolicyTestFiles.ShippedText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new IdentityPolicyProvider(
            Options.Create(new IdentityVerificationOptions { PolicyFile = file, PolicySha256 = IdentityPolicyTestFiles.ShippedCanonicalHash }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
    }

    private IdentityVerificationFlowService Flow(AppDbContext db, ScriptedEvidenceCollector collector, IdentityPolicyProvider policy)
    {
        var limits = new IdentityVerificationOptions();
        var segments = new Mock<IIdentitySegmentService>();
        segments.Setup(service => service.ResolveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceBundles.Observation);
        return new IdentityVerificationFlowService(
            db,
            Mock.Of<IIdentityVerificationAgent>(),
            new RecordingOperatorAlerts(),
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(db, Mock.Of<IAlloggiatiCodeTableService>()),
            new PresenceMemoryStorage(),
            _dataProtection,
            new FakeTimeProvider(Now),
            NullLogger<IdentityVerificationFlowService>.Instance,
            legalTexts: ActivePoseFlowTestVerifier.CreateRegistry().Object,
            featureFlags: new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted),
            identityPolicy: policy,
            biometricModels: new FakeBiometricModelStore(),
            identityOptions: Options.Create(limits),
            presenceIssuer: new PresenceChallengeIssuer(),
            formats: PresenceClosureKit.Formats(),
            frameReader: new FrameSequenceReader(Options.Create(limits), Options.Create(new BiometricModelOptions()), NullLogger<FrameSequenceReader>.Instance),
            evidenceCollector: collector,
            segments: segments.Object,
            decisionEngine: new IdentityDecisionEngine());
    }

    private static PresenceSubmission Submission(string key, string token) =>
        PresenceClosureKit.Submission(
            key,
            token,
            documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture),
            documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture));

    private async Task<(Guid SessionId, Guid BookingId)> SeedReadyStayAsync()
    {
        await using var db = NewContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Org E73", $"e73-{suffix}");
        org.PlanTier = PlanTier.Agency;
        var owner = HostScopeScenario.NewUser($"auth0|e73-{suffix}", org.Id, UserRole.PropertyOwner);
        db.Orgs.Add(org);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var property = HostScopeScenario.NewProperty(org.Id, owner.Id, "Casa E73");
        property.Country = "CH";
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Alba",
            LastName = "Prova",
            Email = $"alba.{suffix}@example.com",
            DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 4, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 270m,
            TotalPrice = 270m,
        };
        db.Bookings.Add(booking);

        var mrz = new DocumentCheck(true, true, false, true, true, true, true, false);
        var check = new StoredDocumentCheck(
            mrz,
            Sha256(PresenceClosureKit.FrontPicture),
            Now.UtcDateTime.AddMinutes(-1),
            BackSha256: Sha256(PresenceClosureKit.BackPicture),
            Front: new StoredDocumentPicture(true, true, null, null, GeometryOk: true, ClassifiedSide: DocumentSides.Front, DHash: "0f1e2d3c4b5a6978"),
            Back: new StoredDocumentPicture(null, true, mrz, null, GeometryOk: true, ClassifiedSide: DocumentSides.Back, DHash: "8796a5b4c3d2e1f0"),
            TwoSided: true);
        var session = new GuestCheckInSession
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            Status = GuestCheckInSessionStatus.InCompilazione,
            ExpiresAt = Now.UtcDateTime.AddDays(7),
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
            BiometricConsentAt = Now.UtcDateTime.AddMinutes(-2),
            BiometricConsentVersion = "TEST-biometric-v1",
            BiometricConsentTextHash = new string('a', 64),
            IdentityOpenedAt = Now.UtcDateTime.AddMinutes(-3),
            IdentityChoice = IdentityChoices.Automatic,
            IdentityChannel = IdentityChannels.AndroidChrome,
            IdentityFormatId = "it-cie-3.0",
            DocumentCheckJson = JsonSerializer.Serialize(check, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();
        return (session.Id, booking.Id);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
