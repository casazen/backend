using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Identity;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E73/F01/US02/T03 (NF-195, AC6) on real PostgreSQL, what memory cannot show: every query of the hourly retention translates on Npgsql (the filtered index of the live evidence,
/// the subqueries of the decided bookings and of the sessions in use, the jsonb of the checks of the document, the stays of the logs) and runs, the abandoned session is closed
/// under the lock of its row (<c>SELECT … FOR UPDATE</c> in a transaction), each organization is purged with its own rows, the status of the platform administrators counts
/// nothing overdue afterwards, and a second run deletes nothing.
/// </summary>
public sealed class IdentityRetentionPostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly FailingMemoryStorage _storage = new();
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        if (!PostgresTestServer.IsAvailable)
            return;

        _database = await PostgresTestDatabase.CreateMigratedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task IdentityRetentionPostgres_ThePurgeQueriesTranslateAndRun_OrgByOrg()
    {
        var a = await SeedOrgAsync("A");
        var b = await SeedOrgAsync("B");

        var expired = await EvidenceAsync(a, a.CurrentStay, Now.UtcDateTime.AddMinutes(-1));
        var live = await EvidenceAsync(a, a.CurrentStay, Now.UtcDateTime.AddHours(5));
        var abandoned = await SessionAsync(a, a.CurrentStay, openedAt: Now.UtcDateTime.AddHours(-25), expiresAt: Now.UtcDateTime.AddDays(5));
        var image = await DocumentImageAsync(a, a.CurrentStay);
        var oldLog = await LogAsync(a.OrgId, a.OldStay, a.GuestId, new DateTime(2025, 10, 30, 9, 0, 0, DateTimeKind.Utc));
        var stale = await SessionAsync(a, a.OldStay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(-40));

        await SessionAsync(b, b.CurrentStay, openedAt: Now.UtcDateTime.AddHours(-1), expiresAt: Now.UtcDateTime.AddDays(5));
        var imageOfB = await DocumentImageAsync(b, b.CurrentStay);
        var logOfB = await LogAsync(b.OrgId, b.CurrentStay, b.GuestId, Now.UtcDateTime.AddDays(-1));
        var crossed = await LogAsync(b.OrgId, a.OldStay, a.GuestId, new DateTime(2025, 10, 30, 9, 0, 0, DateTimeKind.Utc));

        var counts = await Service().PurgeAsync(Now.UtcDateTime, CancellationToken.None);

        Assert.Equal((1, 1, 1, 1), (counts.ExpiredEvidence, counts.AbandonedSessions, counts.AbandonedDocumentImages, counts.VerificationLogsDeleted));
        Assert.True(counts.DocumentChecksCleared >= 1);

        await using var read = NewContext();
        var evidence = await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.Equal(IdentityEvidenceDeletionReasons.Expired, evidence[expired.Id].DeletionReason);
        Assert.Null(evidence[live.Id].DeletedAt);
        Assert.False(_storage.Objects.ContainsKey(expired.StorageKey));

        var closed = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == abandoned.Id);
        Assert.Equal(IdentityOutcomeCodes.Abandoned, closed.IdentityOutcomeCode);
        Assert.NotNull(closed.IdentityClosedAt);
        Assert.Null((await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == stale.Id)).DocumentCheckJson);

        Assert.False(await read.DocumentImages.IgnoreQueryFilters().AnyAsync(row => row.Id == image.Id));
        Assert.True(await read.DocumentImages.IgnoreQueryFilters().AnyAsync(row => row.Id == imageOfB.Id));
        Assert.True(_storage.Objects.ContainsKey(imageOfB.FrontImageUrl));

        var logs = await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Select(log => log.Id).ToListAsync();
        Assert.DoesNotContain(oldLog.Id, logs);
        Assert.Contains(logOfB.Id, logs);
        Assert.Contains(crossed.Id, logs);

        var status = await Service().StatusAsync(CancellationToken.None);
        Assert.Equal((0, 0), (status.OverdueEvidence, status.OverdueDocumentImages));
        Assert.Equal(IdentityRetentionCounts.None, await Service().PurgeAsync(Now.UtcDateTime, CancellationToken.None));
    }

    private AppDbContext NewContext() => new(_database!.CreateOptions(), null, _dataProtection);

    private IdentityRetentionService Service() =>
        new(
            NewContext(),
            _storage,
            new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted),
            Options.Create(new IdentityVerificationOptions()),
            new IdentityRetentionRunLog(),
            new FakeTimeProvider(Now),
            NullLogger<IdentityRetentionService>.Instance);

    private async Task<SeededOrg> SeedOrgAsync(string name)
    {
        await using var db = NewContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg($"Org {name}", $"e73-{name.ToLowerInvariant()}-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|e73-{suffix}", org.Id, UserRole.PropertyOwner);
        db.Orgs.Add(org);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var property = HostScopeScenario.NewProperty(org.Id, owner.Id, $"Casa {name}");
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Alba",
            LastName = "Prova",
            Email = $"alba.{suffix}@example.com",
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        var current = Booking(org.Id, property.Id, guest.Id, new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc));
        var old = Booking(org.Id, property.Id, guest.Id, new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        db.Bookings.AddRange(current, old);
        await db.SaveChangesAsync();
        return new SeededOrg(org.Id, guest.Id, current.Id, old.Id);
    }

    private static Booking Booking(Guid orgId, Guid propertyId, Guid guestId, DateTime checkOut) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        GuestId = guestId,
        CheckInDate = checkOut.AddDays(-3),
        CheckOutDate = checkOut,
        NumberOfGuests = 1,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        BasePrice = 270m,
        TotalPrice = 270m,
    };

    private async Task<IdentityReviewEvidence> EvidenceAsync(SeededOrg org, Guid bookingId, DateTime expiresAt)
    {
        await using var db = NewContext();
        var id = Guid.NewGuid();
        var row = new IdentityReviewEvidence
        {
            Id = id,
            OrgId = org.OrgId,
            BookingId = bookingId,
            GuestId = org.GuestId,
            CheckInSessionId = Guid.NewGuid(),
            Kind = IdentityEvidenceKind.Frame,
            StorageKey = IdentityReviewEvidenceStore.StorageKeyOf(org.OrgId, bookingId, id),
            ContentType = IdentityReviewEvidenceStore.JpegContentType,
            Sha256 = new string('a', 64),
            CreatedAt = expiresAt.AddHours(-48),
            ExpiresAt = expiresAt,
        };
        db.Set<IdentityReviewEvidence>().Add(row);
        await db.SaveChangesAsync();
        _storage.Objects[row.StorageKey] = [1, 2, 3];
        return row;
    }

    private async Task<GuestCheckInSession> SessionAsync(SeededOrg org, Guid bookingId, DateTime? openedAt, DateTime expiresAt)
    {
        await using var db = NewContext();
        var check = new StoredPresenceChallenge(Guid.NewGuid(), [PresenceActions.TurnLeft], Now.AddMinutes(-2), Now.AddMinutes(-1), 1);
        var json = "{\"check\":{\"mrzRead\":true,\"checkDigitsValid\":true,\"holderIsMinor\":false},\"photoSha256\":\"AB\",\"checkedAt\":\"2026-11-26T09:00:00Z\",\"presence\":"
                   + JsonSerializer.Serialize(check, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "}";
        var session = new GuestCheckInSession
        {
            OrgId = org.OrgId,
            BookingId = bookingId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Status = GuestCheckInSessionStatus.InCompilazione,
            ExpiresAt = expiresAt,
            IdentityOpenedAt = openedAt,
            DocumentCheckJson = json,
            CreatedAt = Now.UtcDateTime.AddDays(-2),
            UpdatedAt = Now.UtcDateTime.AddDays(-2),
        };
        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task<DocumentImage> DocumentImageAsync(SeededOrg org, Guid bookingId)
    {
        await using var db = NewContext();
        var image = new DocumentImage
        {
            OrgId = org.OrgId,
            GuestId = org.GuestId,
            BookingId = bookingId,
            FrontImageUrl = StorageKeys.GuestDocument(org.OrgId, org.GuestId, $"{Guid.NewGuid():N}.bin"),
            UploadedAt = Now.UtcDateTime.AddHours(-26),
            ProcessedAt = Now.UtcDateTime.AddHours(-26),
        };
        db.DocumentImages.Add(image);
        await db.SaveChangesAsync();
        _storage.Objects[image.FrontImageUrl] = [4, 5, 6];
        return image;
    }

    private async Task<BiometricVerificationLog> LogAsync(Guid orgId, Guid bookingId, Guid guestId, DateTime createdAt)
    {
        await using var db = NewContext();
        var log = new BiometricVerificationLog
        {
            OrgId = orgId,
            BookingId = bookingId,
            GuestId = guestId,
            Outcome = BiometricOutcome.Escalate,
            Method = VerificationMethod.Biometric,
            Attempts = 1,
            ModelName = "SFace",
            ModelVersion = "2021dec",
            CreatedAt = createdAt,
        };
        db.BiometricVerificationLogs.Add(log);
        await db.SaveChangesAsync();
        return log;
    }

    private sealed record SeededOrg(Guid OrgId, Guid GuestId, Guid CurrentStay, Guid OldStay);
}
