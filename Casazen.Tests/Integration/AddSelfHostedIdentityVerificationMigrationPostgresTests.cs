using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E70/F01/US01/T02 on real PostgreSQL: <c>AddSelfHostedIdentityVerification</c>, the one migration of the self-hosted identity verification (NF-188, NF-194,
/// NF-195). The rows that exist keep the legacy path (no guided retry, decision mode <c>Legacy</c>, nothing of the new columns); the key of an attempt is unique
/// per booking, the last line of defence of the idempotent closure; the two keys of the segments refuse a second row and accept the row of every property next
/// to the row of one property; the evidence is a row of ids, codes, a hash and times, found by the purge through its filtered index. The rows written before the
/// migration name only the columns of that schema (<see cref="LegacyOrgRows"/> and siblings, and the two statements below, all checked without a database by
/// <c>LegacyRowsSchemaTests</c>).
/// </summary>
public class AddSelfHostedIdentityVerificationMigrationPostgresTests : IAsyncLifetime
{
    private const string Thresholds = """{"Accept":0.85,"Escalate":0.7}""";
    private static readonly DateTime Now = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddSelfHostedIdentityVerificationMigration_ExistingRows_ReadAsBefore()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var booking = Guid.NewGuid();
        var session = Guid.NewGuid();
        var log = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            await _database.MigrateEmptyToAsync(PreviousMigration(db));
            await LegacyOrgRows.InsertAsync(db, new OrgEntity
            {
                Id = org,
                Name = "Org E70",
                Slug = $"e70-{org:N}",
                DisplayName = "Org E70",
                ContactEmail = "e70@example.com",
                PlanTier = PlanTier.Agency,
                IsActive = true,
            });
            await LegacyPropertyRows.InsertAsync(db, new Property
            {
                Id = property,
                OrgId = org,
                OwnerId = "auth0|e70",
                Name = "Casa E70",
                Address = "Via E70 " + property.ToString("N"),
                City = "Como",
                PostalCode = "22100",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 90m,
            });
            await LegacyGuestRows.InsertAsync(db, new Guest
            {
                Id = guest,
                OrgId = org,
                FirstName = "Alba",
                LastName = "Prova",
                Email = $"alba.{guest:N}@example.com",
            });
            await LegacyBookingRows.InsertAsync(db, new Booking
            {
                Id = booking,
                PropertyId = property,
                OrgId = org,
                GuestId = guest,
                CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 12, 4, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
            });
            await db.Database.ExecuteSqlAsync(LegacySessionStatement(session, booking, org));
            await db.Database.ExecuteSqlAsync(LegacyLogStatement(log, org, booking, guest));

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var row = await after.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == session);
        Assert.Equal(0, row.IdentityGuidedRetries);
        Assert.Equal(BiometricOutcome.Accept, row.BiometricOutcome);
        Assert.True(new object?[]
        {
            row.IdentityGuestId, row.IdentityOpenedAt, row.IdentityNoticeVersion, row.IdentityNoticeTextHash, row.IdentityChoice, row.IdentityChoiceAt,
            row.IdentityChannel, row.IdentityFormatId, row.IdentityLastAttemptKey, row.IdentityLastAttemptResult, row.IdentityClosedAt, row.IdentityOutcomeCode,
        }.All(value => value is null));

        var decided = await after.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == log);
        Assert.Equal(IdentityDecisionMode.Legacy, decided.DecisionMode);
        Assert.Equal(BiometricOutcome.Accept, decided.Outcome);
        Assert.Equal(VerificationMethod.Biometric, decided.Method);
        Assert.True(new object?[] { decided.PolicyVersion, decided.PolicyHash, decided.ShadowOutcome, decided.FormatId, decided.Channel, decided.IdempotencyKey }
            .All(value => value is null));
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    /// <summary>Data: two processes that close the same attempt (the same key, the same booking) write one row; the legacy rows, without a key, are not limited.</summary>
    [PostgresFact]
    public async Task AddSelfHostedIdentityVerificationMigration_DuplicateIdempotencyKeyIsRejected()
    {
        var world = await MigratedWorldAsync();
        const string key = "0b4a3c1e-9f2d-4d8e-a1b2-3c4d5e6f7a8b";
        await using (var db = _database!.CreateContext())
        {
            db.BiometricVerificationLogs.Add(Log(world, key));
            await db.SaveChangesAsync();
        }

        await using (var second = _database.CreateContext())
        {
            second.BiometricVerificationLogs.Add(Log(world, key));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(BiometricVerificationLog.IdempotencyIndexName, postgres.ConstraintName);
        }

        // The legacy path writes no key: any number of its rows; another key of the same booking, and the same key of another booking, are other attempts.
        await using (var more = _database.CreateContext())
        {
            more.BiometricVerificationLogs.AddRange(Log(world, null), Log(world, null), Log(world, "a7f3c2d1-another-attempt"));
            more.BiometricVerificationLogs.Add(Log(world with { BookingId = world.OtherBookingId }, key));
            await more.SaveChangesAsync();
        }

        await using var read = _database.CreateContext();
        Assert.Equal(1, await read.BiometricVerificationLogs.IgnoreQueryFilters().CountAsync(l => l.BookingId == world.BookingId && l.IdempotencyKey == key));
        Assert.Equal(4, await read.BiometricVerificationLogs.IgnoreQueryFilters().CountAsync(l => l.BookingId == world.BookingId));
        var stored = await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.IdempotencyKey == key && l.BookingId == world.BookingId);
        Assert.Equal(
            ("identity-policy-2026.11-1", IdentityDecisionMode.Observation, BiometricOutcome.Accept, "it-cie-3", IdentityChannels.AndroidChrome),
            (stored.PolicyVersion, stored.DecisionMode, stored.ShadowOutcome, stored.FormatId, stored.Channel));
    }

    [PostgresFact]
    public async Task AddSelfHostedIdentityVerificationMigration_SegmentKeys_RefuseASecondRow_AndAcceptTheOrgAndThePropertyRow()
    {
        var world = await MigratedWorldAsync();
        await using (var db = _database!.CreateContext())
        {
            // The row of every property and the row of one property, the same format and channel: both are kept.
            db.IdentitySegmentSettings.AddRange(Segment(world.OrgId, null), Segment(world.OrgId, world.PropertyId));
            await db.SaveChangesAsync();
        }

        await AssertRefusedAsync(Segment(world.OrgId, null), IdentitySegmentSetting.OrgWideIndexName);
        await AssertRefusedAsync(Segment(world.OrgId, world.PropertyId), IdentitySegmentSetting.PropertyIndexName);

        await using (var db = _database.CreateContext())
        {
            // Another channel is another segment; a row written without mode and assurance takes the defaults of the columns.
            db.IdentitySegmentSettings.Add(Segment(world.OrgId, null, IdentityChannels.IosSafari));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "IdentitySegmentSettings" ("Id", "OrgId", "PropertyId", "FormatId", "Channel", "CreatedAt", "UpdatedAt")
                VALUES ({world.DefaultsSegmentId}, {world.OrgId}, NULL, 'it-cie-3', {IdentityChannels.DesktopFirefox}, now(), now());
                """);
        }

        await using var read = _database.CreateContext();
        Assert.Equal(4, await read.IdentitySegmentSettings.IgnoreQueryFilters().CountAsync(s => s.OrgId == world.OrgId));
        var defaults = await read.IdentitySegmentSettings.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == world.DefaultsSegmentId);
        Assert.Equal((IdentitySegmentMode.Observation, IdentityAssuranceLevels.Consistency), (defaults.Mode, defaults.RequiredAssurance));
        var qualified = await read.IdentitySegmentSettings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.PropertyId == world.PropertyId && s.Channel == IdentityChannels.AndroidChrome);
        Assert.Equal((IdentitySegmentMode.Automatic, IdentityAssuranceLevels.Consistency, "identity-policy-2026.11-1"),
            (qualified.Mode, qualified.RequiredAssurance, qualified.QualifiedPolicyVersion));
    }

    [PostgresFact]
    public async Task AddSelfHostedIdentityVerificationMigration_EvidenceRow_IsWritten_AndThePurgeFindsItByItsFilteredIndex()
    {
        var world = await MigratedWorldAsync();
        var evidence = new IdentityReviewEvidence
        {
            OrgId = world.OrgId,
            BookingId = world.BookingId,
            GuestId = world.GuestId,
            CheckInSessionId = world.SessionId,
            Kind = IdentityEvidenceKind.DocumentPortrait,
            StorageKey = $"orgs/{world.OrgId:N}/identity-evidence/{Guid.NewGuid():N}.bin",
            ContentType = "image/jpeg",
            Sha256 = new string('A', 64),
            CreatedAt = Now,
            ExpiresAt = Now.AddHours(48),
        };
        await using (var db = _database!.CreateContext())
        {
            var session = await db.GuestCheckInSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == world.SessionId);
            session.IdentityGuestId = world.GuestId;
            session.IdentityOpenedAt = Now;
            session.IdentityNoticeVersion = "checkin-identity-2026-11";
            session.IdentityNoticeTextHash = new string('B', 64);
            session.IdentityChoice = IdentityChoices.Automatic;
            session.IdentityChoiceAt = Now;
            session.IdentityChannel = IdentityChannels.DesktopChromium;
            session.IdentityFormatId = "it-cie-3";
            session.IdentityGuidedRetries = 1;
            session.IdentityLastAttemptKey = new string('k', IdentityHeaders.IdempotencyKeyMaxLength);
            session.IdentityLastAttemptResult = "retry:presence_challenge_not_followed,document_glare";
            session.IdentityClosedAt = Now.AddMinutes(3);
            session.IdentityOutcomeCode = IdentityOutcomeCodes.FormatUnsupported;
            db.Set<IdentityReviewEvidence>().Add(evidence);
            await db.SaveChangesAsync();
        }

        await using var read = _database.CreateContext();
        var stored = await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == evidence.Id);
        Assert.Equal((IdentityEvidenceKind.DocumentPortrait, "image/jpeg", (DateTime?)null), (stored.Kind, stored.ContentType, stored.DeletedAt));
        var due = await read.IdentityReviewEvidences.IgnoreQueryFilters()
            .Where(e => e.DeletedAt == null && e.ExpiresAt <= Now.AddHours(49))
            .Select(e => e.Id)
            .ToListAsync();
        Assert.Equal([evidence.Id], due);
        var opened = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrgId == world.OrgId && s.IdentityOpenedAt >= Now)
            .Select(s => s.IdentityOutcomeCode)
            .SingleAsync();
        Assert.Equal(IdentityOutcomeCodes.FormatUnsupported, opened);

        Assert.Contains("WHERE (\"DeletedAt\" IS NULL)", await IndexDefinitionAsync(read, IdentityReviewEvidence.LiveExpiryIndexName), StringComparison.Ordinal);
        Assert.Contains("WHERE (\"IdempotencyKey\" IS NOT NULL)", await IndexDefinitionAsync(read, BiometricVerificationLog.IdempotencyIndexName), StringComparison.Ordinal);
        Assert.Contains("WHERE (\"PropertyId\" IS NULL)", await IndexDefinitionAsync(read, IdentitySegmentSetting.OrgWideIndexName), StringComparison.Ordinal);
        Assert.Contains("WHERE (\"PropertyId\" IS NOT NULL)", await IndexDefinitionAsync(read, IdentitySegmentSetting.PropertyIndexName), StringComparison.Ordinal);
    }

    /// <summary>A check-in session as the schema before the migration had it: the legacy path, a verification accepted at the first attempt.</summary>
    internal static FormattableString LegacySessionStatement(Guid id, Guid bookingId, Guid orgId) => $"""
        INSERT INTO "GuestCheckInSessions" (
            "Id", "BookingId", "OrgId", "TokenHash", "ExpiresAt", "Status", "BiometricAttempts", "BiometricOutcome", "CreatedAt", "UpdatedAt")
        VALUES ({id}, {bookingId}, {orgId}, {id.ToString("N")}, {Now.AddDays(7)}, 1, 1, 0, now(), now());
        """;

    /// <summary>The log row of that verification, with the columns of the schema before the migration (the agent decided, no policy, no key).</summary>
    internal static FormattableString LegacyLogStatement(Guid id, Guid orgId, Guid bookingId, Guid guestId) => $"""
        INSERT INTO "BiometricVerificationLogs" (
            "Id", "OrgId", "BookingId", "GuestId", "Outcome", "Attempts", "ModelName", "ModelVersion", "ThresholdsJson", "Provisional", "CreatedAt")
        VALUES ({id}, {orgId}, {bookingId}, {guestId}, 0, 1, 'SFace', '2021dec', CAST({Thresholds} AS jsonb), true, now());
        """;

    private sealed record World(Guid OrgId, Guid PropertyId, Guid GuestId, Guid BookingId, Guid OtherBookingId, Guid SessionId, Guid DefaultsSegmentId);

    /// <summary>A database at the latest migration with an org, its owner, a property, a guest, two bookings and a check-in session, written through the model.</summary>
    private async Task<World> MigratedWorldAsync()
    {
        await _database!.MigrateEmptyToAsync();
        await using var db = _database.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Org E70", $"e70-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|e70-{suffix}", org.Id, UserRole.PropertyOwner);
        db.Orgs.Add(org);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var property = HostScopeScenario.NewProperty(org.Id, owner.Id, "Casa E70");
        var guest = new Guest { OrgId = org.Id, FirstName = "Alba", LastName = "Prova", Email = $"alba.{suffix}@example.com" };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        var booking = NewBooking(org.Id, property.Id, guest.Id, 1);
        var other = NewBooking(org.Id, property.Id, guest.Id, 10);
        db.Bookings.AddRange(booking, other);
        var session = new GuestCheckInSession
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            Status = GuestCheckInSessionStatus.InCompilazione,
            ExpiresAt = Now.AddDays(7),
        };
        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();
        return new World(org.Id, property.Id, guest.Id, booking.Id, other.Id, session.Id, Guid.NewGuid());
    }

    private static Booking NewBooking(Guid orgId, Guid propertyId, Guid guestId, int fromDay) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        GuestId = guestId,
        CheckInDate = new DateTime(2026, 12, fromDay, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 12, fromDay + 3, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        BasePrice = 270m,
        TotalPrice = 270m,
    };

    private static BiometricVerificationLog Log(World world, string? idempotencyKey) => new()
    {
        OrgId = world.OrgId,
        BookingId = world.BookingId,
        GuestId = world.GuestId,
        Outcome = BiometricOutcome.Escalate,
        Attempts = 1,
        ModelName = "SFace",
        ModelVersion = "2021dec",
        ThresholdsJson = Thresholds,
        PolicyVersion = idempotencyKey is null ? null : "identity-policy-2026.11-1",
        PolicyHash = idempotencyKey is null ? null : new string('C', 64),
        DecisionMode = idempotencyKey is null ? IdentityDecisionMode.Legacy : IdentityDecisionMode.Observation,
        ShadowOutcome = idempotencyKey is null ? null : BiometricOutcome.Accept,
        FormatId = idempotencyKey is null ? null : "it-cie-3",
        Channel = idempotencyKey is null ? null : IdentityChannels.AndroidChrome,
        IdempotencyKey = idempotencyKey,
        CreatedAt = Now,
    };

    private static IdentitySegmentSetting Segment(Guid orgId, Guid? propertyId, string channel = IdentityChannels.AndroidChrome) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        FormatId = "it-cie-3",
        Channel = channel,
        Mode = propertyId is null ? IdentitySegmentMode.Observation : IdentitySegmentMode.Automatic,
        RequiredAssurance = IdentityAssuranceLevels.Consistency,
        QualifiedPolicyVersion = propertyId is null ? null : "identity-policy-2026.11-1",
        QualifiedPolicyHash = propertyId is null ? null : new string('D', 64),
        QualifiedWeightsHash = propertyId is null ? null : new string('E', 64),
        QualificationReportSha256 = propertyId is null ? null : new string('F', 64),
        QualifiedAt = propertyId is null ? null : Now,
        QualifiedByUserId = propertyId is null ? null : "auth0|e70-admin",
        CreatedAt = Now,
        UpdatedAt = Now,
        UpdatedByUserId = "auth0|e70-admin",
    };

    private async Task AssertRefusedAsync(IdentitySegmentSetting duplicate, string indexName)
    {
        await using var db = _database!.CreateContext();
        db.IdentitySegmentSettings.Add(duplicate);
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(refused.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, indexName), (postgres.SqlState, postgres.ConstraintName));
    }

    private static Task<string> IndexDefinitionAsync(AppDbContext db, string indexName) =>
        db.Database.SqlQuery<string>($"""SELECT indexdef::text AS "Value" FROM pg_indexes WHERE indexname = {indexName}""").SingleAsync();

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddSelfHostedIdentityVerification", StringComparison.Ordinal));
        Assert.True(index > 0, "AddSelfHostedIdentityVerification migration not found.");
        return all[index - 1];
    }
}
