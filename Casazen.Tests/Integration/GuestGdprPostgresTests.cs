using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-15 (A5-12, A5-13, A5-14, A5-15, A9-18) on real PostgreSQL: the erasure leaves no personal field on the guest and
/// the guests of its stays (checked by reflection, so a new field cannot be forgotten) and deletes the scan object; the
/// export has every section; the host can never grant the marketing consent; the check-in records the consent with its
/// version; the retention job applies each configured period and deletes nothing without configuration.
/// </summary>
/// <remarks>
/// The tests share one database and the retention job looks at every org: each test checks only its own rows and the
/// storage calls on its own keys.
/// </remarks>
public class GuestGdprPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string OwnerRole = "PropertyOwner";
    private const string DocumentNumber = "CA12345XY";

    /// <summary>Properties of <see cref="Guest"/> that are not personal data of the guest, with the reason.</summary>
    private static readonly Dictionary<string, string> GuestNonPersonal = new()
    {
        [nameof(Guest.Id)] = "pseudonymous key, kept for the bookings",
        [nameof(Guest.OrgId)] = "tenant",
        [nameof(Guest.Org)] = "navigation",
        [nameof(Guest.Bookings)] = "navigation",
        [nameof(Guest.AlloggiatiWebReports)] = "navigation",
        [nameof(Guest.DataProcessingConsentDate)] = "time of a legacy consent, proof without identity",
        [nameof(Guest.ConsentDate)] = "time of the notice, proof without identity",
        [nameof(Guest.ConsentVersion)] = "version of the notice, proof without identity",
        [nameof(Guest.MarketingConsentDate)] = "time of the last marketing change",
        [nameof(Guest.DataRetentionExpiryDate)] = "processing metadata",
        [nameof(Guest.DataProcessingPurpose)] = "processing metadata",
        [nameof(Guest.ErasureRequested)] = "processing metadata",
        [nameof(Guest.ErasureRequestedDate)] = "processing metadata",
        [nameof(Guest.DataAnonymizedDate)] = "processing metadata",
        [nameof(Guest.AlloggiatiDataErasedAt)] = "processing metadata",
        [nameof(Guest.IsDeleted)] = "processing metadata",
        [nameof(Guest.DeletedAt)] = "processing metadata",
        [nameof(Guest.DeletionReason)] = "written by the host, documented as free of personal data",
        [nameof(Guest.CreatedAt)] = "processing metadata",
        [nameof(Guest.UpdatedAt)] = "processing metadata",
    };

    /// <summary>Properties of <see cref="StayGuest"/> that are not personal data of the guest.</summary>
    private static readonly HashSet<string> StayGuestNonPersonal =
    [
        nameof(StayGuest.Id), nameof(StayGuest.BookingId), nameof(StayGuest.Booking), nameof(StayGuest.OrgId),
        nameof(StayGuest.GuestId), nameof(StayGuest.Guest), nameof(StayGuest.Position), nameof(StayGuest.Type),
        nameof(StayGuest.DataSource), nameof(StayGuest.EnteredByUserId), nameof(StayGuest.AnonymizedAt),
        nameof(StayGuest.CreatedAt), nameof(StayGuest.UpdatedAt),
    ];

    private readonly CasazenWebApplicationFactory _factory;

    public GuestGdprPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task EraseGuestDataAsync_GuestWithScanCompanionsAndConsents_ClearsEveryPersonalFieldAndDeletesTheScanObject()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        var storage = new Mock<IFileStorage>();

        await using (var scope = NewScope(out var db))
            await NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta art. 17", "auth0|host");

        await using (var scope = NewScope(out var db))
        {
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            AssertNoPersonalData(guest);
            Assert.True(guest.IsDeleted);
            Assert.True(guest.ErasureRequested);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, guest.DataAnonymizedDate);

            // CO-12 rule: the booker's own row and every companion of the stay, family and group members alike.
            var stayGuests = await db.StayGuests.IgnoreQueryFilters().AsNoTracking().Where(s => s.BookingId == seeded.BookingId).ToListAsync();
            Assert.Equal(3, stayGuests.Count);
            Assert.All(stayGuests, AssertNoPersonalData);
            Assert.Equal([StayGuestType.HeadOfFamily, StayGuestType.FamilyMember, StayGuestType.FamilyMember], stayGuests.OrderBy(s => s.Position).Select(s => s.Type));

            var consents = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.GuestId == seeded.GuestId).ToListAsync();
            Assert.Equal(2, consents.Count);
            Assert.All(consents, r => Assert.Equal((null, null), (r.IpAddress, r.Note)));
            Assert.All(consents, r => Assert.NotEqual(string.Empty, r.Version));

            var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == seeded.BookingId);
            Assert.Equal(string.Empty, booking.SpecialRequests);
            Assert.Equal(480m, booking.TotalPrice);
            var session = await db.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.BookingId == seeded.BookingId);
            Assert.Equal(GuestCheckInSessionStatus.Scaduto, session.Status);

            var audit = Assert.Single(await AuditAsync(db, seeded.GuestId));
            Assert.Equal(
                (GuestPrivacyAuditAction.Erased, "auth0|host", 3, 1, seeded.OrgId),
                (audit.Action, audit.ActorUserId, audit.StayGuestsAnonymized, audit.FilesDeleted, audit.OrgId));
        }

        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, seeded.ScanKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task EraseGuestDataAsync_CalledTwice_ChangesAndAuditsOnlyOnce()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        var storage = new Mock<IFileStorage>();

        await using (var scope = NewScope(out var db))
            await NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta", "auth0|host");
        clock.Advance(TimeSpan.FromDays(1));
        await using (var scope = NewScope(out var db))
            await NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta", "auth0|host");

        await using (var scope = NewScope(out var db))
        {
            Assert.Single(await AuditAsync(db, seeded.GuestId));
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            Assert.Equal(new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc), guest.DeletedAt);
        }

        storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task EraseGuestDataAsync_ScanSharedWithASnapshotOfTheGuest_KeepsTheObjectForTheOtherRecord()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        Guid snapshotId;
        await using (var scope = NewScope(out var db))
        {
            var original = await db.Guests.IgnoreQueryFilters().SingleAsync(g => g.Id == seeded.GuestId);
            var snapshot = original.CreateSnapshot(DateTime.UtcNow);
            db.Guests.Add(snapshot);
            await db.SaveChangesAsync();
            snapshotId = snapshot.Id;
        }

        var storage = new Mock<IFileStorage>();
        await using (var scope = NewScope(out var db))
            await NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta", "auth0|host");

        storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await using (var scope = NewScope(out var db))
        {
            Assert.Null((await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId)).DocumentScanUrl);
            Assert.Equal(seeded.ScanKey, (await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == snapshotId)).DocumentScanUrl);
        }
    }

    [PostgresFact]
    public async Task EraseGuestDataAsync_OpenBooking_Throws409AndChangesNothing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(
            checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            status: BookingStatus.Confirmed);
        var storage = new Mock<IFileStorage>();

        await using (var scope = NewScope(out var db))
        {
            var error = await Assert.ThrowsAsync<Core.Exceptions.DomainConflictException>(() =>
                NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta", "auth0|host"));
            Assert.Equal("guest_has_open_bookings", error.Code);
        }

        await using (var scope = NewScope(out var db))
        {
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            Assert.Equal(("Giulia", DocumentNumber, seeded.ScanKey), (guest.FirstName, guest.DocumentNumber, guest.DocumentScanUrl));
            Assert.Empty(await AuditAsync(db, seeded.GuestId));
        }

        storage.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task EraseGuestDataAsync_OverdueActiveBooking_Throws409AndChangesNothing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(
            checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            status: BookingStatus.CheckedIn);
        var storage = new Mock<IFileStorage>();

        await using (var scope = NewScope(out var db))
        {
            var error = await Assert.ThrowsAsync<Core.Exceptions.DomainConflictException>(() =>
                NewGdprService(db, storage.Object, clock).EraseGuestDataAsync(seeded.OrgId, seeded.GuestId, "Richiesta", "auth0|host"));
            Assert.Equal("guest_has_open_bookings", error.Code);
        }

        await using (var scope = NewScope(out var db))
        {
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            Assert.Equal(("Giulia", DocumentNumber, seeded.ScanKey), (guest.FirstName, guest.DocumentNumber, guest.DocumentScanUrl));
            Assert.Empty(await AuditAsync(db, seeded.GuestId));
        }

        storage.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task ExportGuestData_AsOwner_ReturnsEverySectionWithTheDocumentInClearAndAuditsIt()
    {
        var owner = $"auth0|co15-export-{Guid.NewGuid():N}";
        var seeded = await SeedStayAsync(checkOut: TimeProvider.System.TodayInRome().AddDays(-30), owner: owner);

        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        var response = await client.GetAsync($"/api/gdpr/guests/{seeded.GuestId}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(Core.Models.GuestDataExport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("giulia.bianchi@example.com", root.GetProperty("subject").GetProperty("email").GetString());
        Assert.Equal("Via Roma 1", root.GetProperty("subject").GetProperty("address").GetString());
        Assert.Equal("1985-04-12", root.GetProperty("birth").GetProperty("dateOfBirth").GetString());
        Assert.Equal("Italiana", root.GetProperty("birth").GetProperty("nationality").GetString());
        var document = root.GetProperty("document");
        Assert.Equal(DocumentNumber, document.GetProperty("number").GetString());
        Assert.Equal("IdentityCard", document.GetProperty("type").GetString());
        Assert.Equal("2031-05-01", document.GetProperty("expiryDate").GetString());
        Assert.True(document.GetProperty("hasScan").GetBoolean());
        var booking = Assert.Single(root.GetProperty("bookings").EnumerateArray());
        Assert.Equal(seeded.BookingId, booking.GetProperty("id").GetGuid());
        Assert.Equal("Senza glutine", booking.GetProperty("specialRequests").GetString());
        Assert.Equal(3, root.GetProperty("stayGuests").GetArrayLength());
        Assert.Equal(1, root.GetProperty("stayGuests").EnumerateArray().Count(s => s.GetProperty("isBooker").GetBoolean()));
        Assert.Contains(root.GetProperty("stayGuests").EnumerateArray(), s => s.GetProperty("firstName").GetString() == "Luca");
        Assert.Single(root.GetProperty("checkInSessions").EnumerateArray());
        var history = root.GetProperty("consents").GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, h => h.GetProperty("purpose").GetString() == "Marketing"
            && h.GetProperty("action").GetString() == "Granted"
            && h.GetProperty("version").GetString() == "marketing-2026-09"
            && h.GetProperty("ipAddress").GetString() == "198.51.100.4");
        var retention = root.GetProperty("processing").GetProperty("retention").EnumerateArray().ToList();
        Assert.Equal(4, retention.Count);
        Assert.All(retention, r => Assert.False(r.GetProperty("configured").GetBoolean()));

        await using var scope = NewScope(out var db);
        var audit = Assert.Single(await AuditAsync(db, seeded.GuestId));
        Assert.Equal((GuestPrivacyAuditAction.Exported, owner), (audit.Action, audit.ActorUserId));
    }

    [PostgresFact]
    public async Task UpdateConsent_HostGrantsMarketing_Returns422AndTheConsentStaysOff()
    {
        var owner = $"auth0|co15-grant-{Guid.NewGuid():N}";
        var seeded = await SeedStayAsync(checkOut: TimeProvider.System.TodayInRome().AddDays(-30), owner: owner, marketingConsent: false);

        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        var response = await client.PutAsJsonAsync($"/api/gdpr/guests/{seeded.GuestId}/consent", new { marketingConsent = true, note = "Ha detto di sì al telefono" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(GdprService.HostGrantForbiddenCode, problem.RootElement.GetProperty("code").GetString());
        await using var scope = NewScope(out var db);
        var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
        Assert.False(guest.MarketingConsent);
        Assert.DoesNotContain(
            await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.GuestId == seeded.GuestId).ToListAsync(),
            r => r.Source == GuestConsentSource.HostOnGuestRequest);
    }

    [PostgresFact]
    public async Task UpdateConsent_HostWithdrawsWithoutNote_Returns422_WithNote_RecordsTheWithdrawalOfTheGrantedVersion()
    {
        var owner = $"auth0|co15-withdraw-{Guid.NewGuid():N}";
        var seeded = await SeedStayAsync(checkOut: TimeProvider.System.TodayInRome().AddDays(-30), owner: owner);
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);

        var withoutNote = await client.PutAsJsonAsync($"/api/gdpr/guests/{seeded.GuestId}/consent", new { marketingConsent = false, note = " " });
        var withNote = await client.PutAsJsonAsync(
            $"/api/gdpr/guests/{seeded.GuestId}/consent", new { marketingConsent = false, note = "Email dell'ospite del 02/09/2026" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutNote.StatusCode);
        using (var problem = JsonDocument.Parse(await withoutNote.Content.ReadAsStringAsync()))
            Assert.Equal(GdprService.WithdrawalNoteRequiredCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, withNote.StatusCode);

        await using var scope = NewScope(out var db);
        Assert.False((await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId)).MarketingConsent);
        var withdrawal = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.GuestId == seeded.GuestId && r.Action == GuestConsentAction.Withdrawn);
        Assert.Equal(
            (GuestConsentPurpose.Marketing, "marketing-2026-09", GuestConsentSource.HostOnGuestRequest, "Email dell'ospite del 02/09/2026", owner),
            (withdrawal.Purpose, withdrawal.Version, withdrawal.Source, withdrawal.Note, withdrawal.RecordedByUserId));
        var audit = Assert.Single(await AuditAsync(db, seeded.GuestId));
        Assert.Equal(GuestPrivacyAuditAction.MarketingConsentWithdrawn, audit.Action);
    }

    [PostgresFact]
    public async Task SubmitAsync_GuestPortalWithVersionsConfigured_RecordsNoticeAndMarketingConsentWithVersion()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero));
        var seeded = await SeedStayAsync(
            checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            marketingConsent: false,
            withStayData: false,
            status: BookingStatus.Confirmed);
        var options = Options.Create(new GdprOptions { PrivacyNoticeVersion = "notice-2026-10", MarketingConsentVersion = "marketing-2026-10" });

        await using (var scope = NewScope(out var db))
        {
            var service = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance, timeProvider: clock, gdprOptions: options);
            var link = await service.IssueLinkAsync(seeded.BookingId, seeded.OrgId);
            var result = await service.SubmitAsync(link.Token, new GuestCheckInSubmitRequest
            {
                Guests = [Booker()],
                MarketingConsent = true,
                ConsentIpAddress = "203.0.113.9",
            });
            Assert.True(result.Success);
        }

        await using (var scope = NewScope(out var db))
        {
            var records = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.GuestId == seeded.GuestId && r.RecordedAt == clock.GetUtcNow().UtcDateTime)
                .OrderBy(r => r.Purpose)
                .ToListAsync();
            Assert.Equal(
                [
                    (GuestConsentPurpose.PrivacyNotice, GuestConsentAction.NoticePresented, "notice-2026-10", "203.0.113.9"),
                    (GuestConsentPurpose.Marketing, GuestConsentAction.Granted, "marketing-2026-10", "203.0.113.9"),
                ],
                records.Select(r => (r.Purpose, r.Action, r.Version, r.IpAddress)));
            Assert.All(records, r => Assert.Equal((seeded.OrgId, GuestConsentSource.GuestPortal), (r.OrgId, r.Source)));
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            Assert.True(guest.MarketingConsent);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, guest.MarketingConsentDate);
        }
    }

    [PostgresFact]
    public async Task ApplyAsync_NoPeriodConfigured_DeletesNothingOfAnyCategory()
    {
        var seeded = await SeedStayAsync(checkOut: new DateTime(2016, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var clock = new FakeTimeProvider(new DateTimeOffset(2046, 1, 10, 9, 0, 0, TimeSpan.Zero));
        var storage = new Mock<IFileStorage>();

        Core.Models.GuestRetentionRunResult result;
        await using (var scope = NewScope(out var db))
            result = await NewRetentionService(db, storage.Object, clock, new GdprRetentionOptions()).ApplyAsync();

        Assert.All(result.Categories, c => Assert.False(c.Configured));
        Assert.Equal(Enum.GetValues<GuestDataCategory>(), result.Categories.Select(c => c.Category));
        storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await using (var scope = NewScope(out var db))
        {
            var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seeded.GuestId);
            Assert.Equal(("Giulia", DocumentNumber, seeded.ScanKey, true), (guest.FirstName, guest.DocumentNumber, guest.DocumentScanUrl, guest.MarketingConsent));
            Assert.Null(guest.DataAnonymizedDate);
            Assert.All(await db.StayGuests.IgnoreQueryFilters().AsNoTracking().Where(s => s.BookingId == seeded.BookingId).ToListAsync(), s => Assert.Null(s.AnonymizedAt));
            Assert.Empty(await AuditAsync(db, seeded.GuestId));
        }
    }

    [PostgresFact]
    public async Task ApplyAsync_PeriodWithoutSource_IsNotApplied()
    {
        var seeded = await SeedStayAsync(checkOut: new DateTime(2016, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var clock = new FakeTimeProvider(new DateTimeOffset(2046, 1, 10, 9, 0, 0, TimeSpan.Zero));
        var storage = new Mock<IFileStorage>();
        var retention = new GdprRetentionOptions { DocumentScans = new RetentionPeriodOptions { Days = 0 } };

        Core.Models.GuestRetentionRunResult result;
        await using (var scope = NewScope(out var db))
            result = await NewRetentionService(db, storage.Object, clock, retention).ApplyAsync();

        Assert.False(result.Categories.Single(c => c.Category == GuestDataCategory.DocumentScans).Configured);
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, seeded.ScanKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task ApplyAsync_PeriodsConfigured_AppliesEachCategoryOnlyAfterItsOwnPeriodAndOnce()
    {
        var checkOut = new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var seeded = await SeedStayAsync(checkOut: checkOut, marketingGrantedAt: new DateTime(2027, 1, 5, 10, 0, 0, DateTimeKind.Utc));
        var storage = new Mock<IFileStorage>();
        var retention = new GdprRetentionOptions
        {
            DocumentScans = new RetentionPeriodOptions { Days = 30, Source = "test: scans" },
            AlloggiatiData = new RetentionPeriodOptions { Years = 1, Source = "test: alloggiati" },
            Marketing = new RetentionPeriodOptions { Months = 18, Source = "test: marketing" },
            FiscalData = new RetentionPeriodOptions { Years = 2, Source = "test: fiscal" },
        };

        // Day 30 after the check-out: still inside every period.
        await RunRetentionAsync(storage, retention, checkOut.AddDays(30));
        var guest = await LoadGuestAsync(seeded.GuestId);
        Assert.Equal(seeded.ScanKey, guest.DocumentScanUrl);

        // Day 31: the scan goes, nothing else; a second run the same day changes nothing.
        await RunRetentionAsync(storage, retention, checkOut.AddDays(31));
        await RunRetentionAsync(storage, retention, checkOut.AddDays(31));
        guest = await LoadGuestAsync(seeded.GuestId);
        Assert.Null(guest.DocumentScanUrl);
        Assert.Equal(DocumentNumber, guest.DocumentNumber);
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, seeded.ScanKey, It.IsAny<CancellationToken>()), Times.Once);

        // One year and a day: the Alloggiati data of the booker and of every guest of the stay, names and contacts stay.
        await RunRetentionAsync(storage, retention, checkOut.AddYears(1).AddDays(1));
        guest = await LoadGuestAsync(seeded.GuestId);
        Assert.Equal(("Giulia", "giulia.bianchi@example.com", string.Empty, null), (guest.FirstName, guest.Email, guest.DocumentNumber, guest.DateOfBirth));
        Assert.NotNull(guest.AlloggiatiDataErasedAt);
        Assert.True(guest.MarketingConsent);
        await using (var scope = NewScope(out var db))
            Assert.All(await db.StayGuests.IgnoreQueryFilters().AsNoTracking().Where(s => s.BookingId == seeded.BookingId).ToListAsync(), AssertNoPersonalData);

        // 18 months after the consent: it expires, with a record of the version it ends.
        await RunRetentionAsync(storage, retention, new DateTime(2028, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        guest = await LoadGuestAsync(seeded.GuestId);
        Assert.False(guest.MarketingConsent);
        await using (var scope = NewScope(out var db))
        {
            var expiry = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(r => r.GuestId == seeded.GuestId && r.Action == GuestConsentAction.Expired);
            Assert.Equal((GuestConsentPurpose.Marketing, "marketing-2026-09", GuestConsentSource.RetentionPolicy), (expiry.Purpose, expiry.Version, expiry.Source));
        }

        // Two years and a day: the whole record is anonymized, not marked deleted.
        await RunRetentionAsync(storage, retention, checkOut.AddYears(2).AddDays(1));
        await RunRetentionAsync(storage, retention, checkOut.AddYears(2).AddDays(2));
        guest = await LoadGuestAsync(seeded.GuestId);
        AssertNoPersonalData(guest);
        Assert.False(guest.IsDeleted);

        await using (var scope = NewScope(out var db))
        {
            var audits = await AuditAsync(db, seeded.GuestId);
            Assert.All(audits, a => Assert.Equal((GuestPrivacyAuditAction.RetentionApplied, null), (a.Action, a.ActorUserId)));
            Assert.Equal(
                [GuestDataCategory.DocumentScans, GuestDataCategory.AlloggiatiData, GuestDataCategory.AlloggiatiData, GuestDataCategory.Marketing, GuestDataCategory.FiscalData],
                audits.Select(a => a.Category!.Value));
            Assert.Equal(3, audits.Where(a => a.Category == GuestDataCategory.AlloggiatiData).Sum(a => a.StayGuestsAnonymized));
        }
    }

    [PostgresFact]
    public async Task ApplyAsync_GuestWithALaterStay_WaitsForTheLatestCheckOut()
    {
        var firstCheckOut = new DateTime(2027, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var seeded = await SeedStayAsync(checkOut: firstCheckOut);
        await AddBookingAsync(seeded, checkOut: firstCheckOut.AddMonths(6));
        var storage = new Mock<IFileStorage>();
        var retention = new GdprRetentionOptions
        {
            DocumentScans = new RetentionPeriodOptions { Days = 30, Source = "test: scans" },
            FiscalData = new RetentionPeriodOptions { Months = 3, Source = "test: fiscal" },
        };

        await RunRetentionAsync(storage, retention, firstCheckOut.AddMonths(4));

        var guest = await LoadGuestAsync(seeded.GuestId);
        Assert.Equal(("Giulia", seeded.ScanKey), (guest.FirstName, guest.DocumentScanUrl));
        Assert.Null(guest.DataAnonymizedDate);
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, seeded.ScanKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task ApplyAsync_OverdueActiveBooking_DoesNotApplyRetention()
    {
        var checkOut = new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var seeded = await SeedStayAsync(checkOut: checkOut, status: BookingStatus.CheckedIn);
        var storage = new Mock<IFileStorage>();
        var retention = new GdprRetentionOptions
        {
            DocumentScans = new RetentionPeriodOptions { Days = 0, Source = "test: scans" },
            AlloggiatiData = new RetentionPeriodOptions { Days = 0, Source = "test: alloggiati" },
            FiscalData = new RetentionPeriodOptions { Days = 0, Source = "test: fiscal" },
        };

        await RunRetentionAsync(storage, retention, checkOut.AddYears(3));

        var guest = await LoadGuestAsync(seeded.GuestId);
        Assert.Equal(("Giulia", DocumentNumber, seeded.ScanKey), (guest.FirstName, guest.DocumentNumber, guest.DocumentScanUrl));
        Assert.Null(guest.DataAnonymizedDate);
        Assert.Null(guest.AlloggiatiDataErasedAt);
        await using (var scope = NewScope(out var db))
        {
            Assert.All(
                await db.StayGuests.IgnoreQueryFilters().AsNoTracking().Where(s => s.BookingId == seeded.BookingId).ToListAsync(),
                s => Assert.Null(s.AnonymizedAt));
            Assert.Empty(await AuditAsync(db, seeded.GuestId));
        }

        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, seeded.ScanKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task RunRetentionAsync(Mock<IFileStorage> storage, GdprRetentionOptions retention, DateTime romeDay)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(romeDay.Date.AddHours(3), TimeSpan.Zero));
        await using var scope = NewScope(out var db);
        await NewRetentionService(db, storage.Object, clock, retention).ApplyAsync();
    }

    private static GuestDataRetentionService NewRetentionService(
        AppDbContext db,
        IFileStorage storage,
        TimeProvider clock,
        GdprRetentionOptions retention) =>
        new(
            db,
            new GuestDataEraser(db, storage, NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions { Retention = retention }),
            clock,
            NullLogger<GuestDataRetentionService>.Instance);

    private static GdprService NewGdprService(AppDbContext db, IFileStorage storage, TimeProvider clock) =>
        new(
            db,
            new GuestRepository(db),
            new GuestDataEraser(db, storage, NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions()),
            clock,
            NullLogger<GdprService>.Instance);

    private AsyncServiceScope NewScope(out AppDbContext db)
    {
        var scope = _factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    private async Task<Guest> LoadGuestAsync(Guid guestId)
    {
        await using var scope = NewScope(out var db);
        return await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == guestId);
    }

    private static Task<List<GuestPrivacyAuditEntry>> AuditAsync(AppDbContext db, Guid guestId) =>
        db.GuestPrivacyAuditEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.GuestId == guestId)
            .OrderBy(a => a.OccurredAt).ThenBy(a => a.Category)
            .ToListAsync();

    private static void AssertNoPersonalData(Guest guest)
    {
        foreach (var property in typeof(Guest).GetProperties())
        {
            if (GuestNonPersonal.ContainsKey(property.Name))
                continue;
            var value = property.GetValue(guest);
            Assert.True(IsErased(value, guest.Id), $"Guest.{property.Name} still holds '{value}' after the anonymization");
        }
    }

    private static void AssertNoPersonalData(StayGuest row)
    {
        foreach (var property in typeof(StayGuest).GetProperties())
        {
            if (StayGuestNonPersonal.Contains(property.Name))
                continue;
            var value = property.GetValue(row);
            Assert.True(IsErased(value, Guid.Empty), $"StayGuest.{property.Name} still holds '{value}' after the anonymization");
        }

        Assert.NotNull(row.AnonymizedAt);
    }

    private static bool IsErased(object? value, Guid guestId) => value switch
    {
        null => true,
        string text => text.Length == 0 || text == GuestDataEraser.AnonymizedName || text == GuestDataEraser.AnonymizedEmail(guestId),
        bool flag => !flag,
        _ => false,
    };

    private static StayGuestInput Booker() => new()
    {
        Type = "SingleGuest",
        FirstName = "Giulia",
        LastName = "Bianchi",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1985, 4, 12, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Firenze",
        BirthProvince = "FI",
        CitizenshipName = "Italia",
        DocumentType = "IdentityCard",
        DocumentNumber = DocumentNumber,
        DocumentIssuePlaceName = "Firenze",
    };

    /// <summary>
    /// A booker with every personal field, its document scan in the private storage, a past stay of three guests (the
    /// booker as head of family and two family members), a check-in link, special requests and two consent events.
    /// </summary>
    private async Task<SeededStay> SeedStayAsync(
        DateTime checkOut,
        string? owner = null,
        bool marketingConsent = true,
        DateTime? marketingGrantedAt = null,
        bool withStayData = true,
        BookingStatus status = BookingStatus.CheckedOut)
    {
        owner ??= $"auth0|co15-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        await using var scope = NewScope(out var db);

        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = "giulia.bianchi@example.com",
            PhoneNumber = "+39 055 123456",
            Address = "Via Roma 1",
            City = "Firenze",
            PostalCode = "50100",
            Country = "Italia",
            Notes = "Arriva tardi",
            Gender = Gender.Female,
            DateOfBirth = new DateTime(1985, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            PlaceOfBirth = "Firenze (FI)",
            Nationality = "Italiana",
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentNumber = DocumentNumber,
            DocumentIssueDate = new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentExpiryDate = new DateTime(2031, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentIssuingCountry = "Firenze",
            ConsentIpAddress = "198.51.100.4",
            MarketingConsent = marketingConsent,
            MarketingConsentDate = marketingConsent ? marketingGrantedAt ?? checkOut.AddDays(-5) : null,
        };
        guest.DocumentScanUrl = StorageKeys.GuestDocument(property.OrgId, guest.Id, "scan.jpg");
        db.Guests.Add(guest);

        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkOut.AddDays(-3),
            CheckOutDate = checkOut,
            NumberOfGuests = 3,
            Status = status,
            Source = BookingSource.Direct,
            BasePrice = 480m,
            TotalPrice = 480m,
            SpecialRequests = "Senza glutine",
        };
        db.Bookings.Add(booking);

        if (withStayData)
        {
            db.StayGuests.AddRange(
                StayGuestRow(booking, 0, StayGuestType.HeadOfFamily, "Giulia", guest.Id, DocumentNumber),
                StayGuestRow(booking, 1, StayGuestType.FamilyMember, "Luca", null, string.Empty),
                StayGuestRow(booking, 2, StayGuestType.FamilyMember, "Sara", null, string.Empty));
            db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = property.OrgId,
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                ExpiresAt = checkOut.AddDays(30),
                Status = GuestCheckInSessionStatus.InCompilazione,
            });
            db.GuestConsentRecords.AddRange(
                new GuestConsentRecord
                {
                    OrgId = property.OrgId,
                    GuestId = guest.Id,
                    Purpose = GuestConsentPurpose.PrivacyNotice,
                    Action = GuestConsentAction.NoticePresented,
                    Version = "notice-2026-09",
                    Source = GuestConsentSource.GuestPortal,
                    IpAddress = "198.51.100.4",
                    RecordedAt = checkOut.AddDays(-5),
                },
                new GuestConsentRecord
                {
                    OrgId = property.OrgId,
                    GuestId = guest.Id,
                    Purpose = GuestConsentPurpose.Marketing,
                    Action = GuestConsentAction.Granted,
                    Version = "marketing-2026-09",
                    Source = GuestConsentSource.GuestPortal,
                    IpAddress = "198.51.100.4",
                    Note = "Consenso dal portale",
                    RecordedAt = checkOut.AddDays(-5),
                });
        }

        await db.SaveChangesAsync();
        return new SeededStay(property.OrgId, property.Id, guest.Id, booking.Id, guest.DocumentScanUrl);
    }

    private async Task AddBookingAsync(SeededStay seeded, DateTime checkOut)
    {
        await using var scope = NewScope(out var db);
        db.Bookings.Add(new Booking
        {
            PropertyId = seeded.PropertyId,
            OrgId = seeded.OrgId,
            GuestId = seeded.GuestId,
            CheckInDate = checkOut.AddDays(-2),
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        });
        await db.SaveChangesAsync();
    }

    private static StayGuest StayGuestRow(Booking booking, int position, StayGuestType type, string firstName, Guid? guestId, string documentNumber) => new()
    {
        BookingId = booking.Id,
        OrgId = booking.OrgId,
        GuestId = guestId,
        Position = position,
        Type = type,
        FirstName = firstName,
        LastName = "Bianchi",
        Gender = position == 1 ? Gender.Male : Gender.Female,
        DateOfBirth = new DateTime(2015 - position, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneCode = "048017",
        BirthComuneName = "Firenze",
        BirthProvince = "FI",
        CitizenshipCode = "100000100",
        CitizenshipName = "Italia",
        DocumentType = documentNumber.Length > 0 ? GuestDocumentType.IdentityCard : null,
        DocumentTypeCode = documentNumber.Length > 0 ? "IDENT" : null,
        DocumentNumber = documentNumber,
        DocumentIssuePlaceCode = documentNumber.Length > 0 ? "048017" : null,
        DocumentIssuePlaceName = documentNumber.Length > 0 ? "Firenze" : string.Empty,
        DataSource = StayGuestDataSource.GuestPortal,
    };

    private sealed record SeededStay(Guid OrgId, Guid PropertyId, Guid GuestId, Guid BookingId, string ScanKey);
}
