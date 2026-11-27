using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E06/F03/US02/T01, E11/F01/US01/T01 (NF-84, FIS-04): the documents Octorate issued are imported only into the org of the connection and linked only to
/// its own bookings, their rejected SDI state and their amount to collect become "To do" items that close when the state changes, an imported document
/// never locks the issuer of its booking nor raises <c>two_issuers_detected</c> (CasaZen is the only issuer; a booking that already has a document issued
/// elsewhere is not issued twice, which the document service decides), and nothing is read while the import is off: its two flags, and the option
/// <c>FiscalDocuments:ImportOctorateDocuments</c> (false by default).
/// </summary>
public class OctorateDocumentImportProofTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OctorateDocument_OtherOrg_NeverImportedOrVisible_Test()
    {
        await using var db = NewDb();
        var (_, bookingA) = await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var (_, bookingB) = await SeedOrgAsync(db, OrgB, "ACC-B", "R-200");
        var client = Client(("ACC-A", [Document("D-1", "R-200"), Document("D-2", "R-100")]));
        var service = Service(db, client.Object);

        var result = await service.ImportAsync(OrgA, since: null);

        Assert.Equal(2, result.Created);
        var rows = await db.FiscalDocuments.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.Equal(OrgA, row.OrgId));
        Assert.Null(rows.Single(r => r.OctorateDocumentId == "D-1").BookingId);
        Assert.Equal(bookingA, rows.Single(r => r.OctorateDocumentId == "D-2").BookingId);
        Assert.DoesNotContain(rows, r => r.BookingId == bookingB);
        client.Verify(c => c.GetDocumentsAsync("ACC-B", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);

        // The org B's caller reads no document of org A through the tenant filter.
        await using var asB = NewDb(db, OrgB);
        Assert.Empty(await asB.FiscalDocuments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_IsReadOnlyIssued_RaisesTheToDos_AndClosesThemWhenTheStateChanges()
    {
        await using var db = NewDb();
        var (_, booking) = await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var activities = Activities();
        var rejected = Document("D-9", "R-100") with { SdiGroup = "SDI_REJECTED", Paid = false };
        var client = Client(("ACC-A", [rejected]));

        await Service(db, client.Object, activities: activities.Object).ImportAsync(OrgA, since: null);

        var row = await db.FiscalDocuments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(FiscalDocumentSource.Octorate, row.Source);
        Assert.Equal(FiscalDocumentStatus.Issued, row.Status);
        Assert.Equal(DocumentKind.Invoice, row.Kind);
        Assert.Equal(FiscalDocumentSdiStatuses.Rejected, row.SdiStatus);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, row.PaymentState);
        Assert.Equal(booking, row.BookingId);
        activities.Verify(a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == OctorateDocumentImport.SdiRejectedReason && r.EntityId == row.Id), It.IsAny<CancellationToken>()), Times.Once);
        activities.Verify(a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == OctorateDocumentImport.ToCollectReason && r.EntityId == row.Id), It.IsAny<CancellationToken>()), Times.Once);

        // A later import: delivered and paid. The two items are closed by the system.
        var openSdi = new InternalActivity { Id = Guid.NewGuid(), OrgId = OrgA };
        var openPayment = new InternalActivity { Id = Guid.NewGuid(), OrgId = OrgA };
        activities.Setup(a => a.FindOpenAsync(OrgA, nameof(ActivityAction.ReviewFiscalDocument), OctorateDocumentImport.SdiEntityType, row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(openSdi);
        activities.Setup(a => a.FindOpenAsync(OrgA, nameof(ActivityAction.ReviewFiscalDocument), OctorateDocumentImport.PaymentEntityType, row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(openPayment);
        var later = Client(("ACC-A", [rejected with { SdiGroup = "DONE", Paid = true }]));

        await Service(db, later.Object, activities: activities.Object).ImportAsync(OrgA, since: Now.UtcDateTime.AddDays(-1));

        Assert.Single(await db.FiscalDocuments.IgnoreQueryFilters().ToListAsync());
        activities.Verify(a => a.CloseAsync(openSdi.Id, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
        activities.Verify(a => a.CloseAsync(openPayment.Id, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: data rule (replaces «two issuers detected»). A booking CasaZen issued for, and one nobody issued for yet: the imported documents are
    /// stored and linked, the lock of CasaZen stays as it was, no booking is locked to Octorate and no "To do" of two issuers is raised. The import only
    /// links the documents: what CasaZen then does with a booking that has a document imported is the document service's (it issues no second document for
    /// it, 409 <c>document_already_issued</c>: <c>FiscalDocumentServiceProofTests.FiscalDocument_BookingWithADocumentIssuedElsewhere_IsNotIssuedAgain_Test</c>).
    /// </summary>
    [Fact]
    public async Task Import_NeverLocksTheIssuer_NorRaisesTwoIssuers_Test()
    {
        await using var db = NewDb();
        var (_, issuedByCasazen) = await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var notIssued = await AddBookingAsync(db, OrgA, "R-101");
        db.BookingDocumentIssuers.Add(new BookingDocumentIssuer
        {
            OrgId = OrgA,
            BookingId = issuedByCasazen,
            Issuer = BookingDocumentIssuerKind.CasaZen,
            LockedAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
        var activities = Activities();

        var result = await Service(db, Client(("ACC-A", [Document("D-3", "R-100"), Document("D-4", "R-101")])).Object, activities.Object)
            .ImportAsync(OrgA, since: null);

        Assert.Equal(2, result.Created);
        var rows = await db.FiscalDocuments.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(issuedByCasazen, rows.Single(r => r.OctorateDocumentId == "D-3").BookingId);
        Assert.Equal(notIssued, rows.Single(r => r.OctorateDocumentId == "D-4").BookingId);
        var locks = await db.BookingDocumentIssuers.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, Assert.Single(locks).Issuer);
        activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == "two_issuers_detected" || r.EntityType == "BookingIssuers"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Import_OffOrOptionFalse_ReadsNothing_AndAFailedReadIsCountedNotThrown()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var client = Client(("ACC-A", [Document("D-1", "R-100")]));

        var off = Service(db, client.Object, flags: new TestFeatureFlags(FeatureFlags.OctorateConnector));
        Assert.Empty(await off.OrgsToImportAsync());
        Assert.Equal(0, (await off.ImportAsync(OrgA, since: null)).Created);

        // The two flags on and the option FiscalDocuments:ImportOctorateDocuments false (the default): the job selects nobody and reads nothing.
        var optionOff = Service(db, client.Object, importOctorateDocuments: false);
        Assert.Empty(await optionOff.OrgsToImportAsync());
        Assert.Equal(0, (await optionOff.ImportAsync(OrgA, since: null)).Created);
        client.Verify(c => c.GetDocumentsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(new Casazen.Core.Options.FiscalDocumentRulesOptions().ImportOctorateDocuments);

        // With the option: the connected orgs, whatever their profile says.
        Assert.Equal(new[] { OrgA }, await Service(db, client.Object).OrgsToImportAsync());

        var failing = new Mock<IOctorateApiClient>();
        failing.Setup(c => c.GetDocumentsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        var result = await Service(db, failing.Object).ImportAsync(OrgA, since: null);
        Assert.Equal(1, result.FailedReads);
        Assert.Empty(await db.FiscalDocuments.IgnoreQueryFilters().ToListAsync());
    }

    /// <summary>
    /// Each structure is read from its own last import: one that fails keeps its date, so the others succeeding never move it, and the documents it changed
    /// meanwhile are not left out (the window is not the date of the whole org).
    /// </summary>
    [Fact]
    public async Task Import_EachStructureIsReadFromItsOwnLastImport_AFailingOneKeepsItsDate_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        await AddStructureAsync(db, OrgA, "ACC-B", "R-200");
        var clock = new FakeTimeProvider(Now);

        // The first read of both structures; each document stores the accommodation it was read from.
        var first = Client(("ACC-A", [Document("D-A", "R-100")]), ("ACC-B", [Document("D-B", "R-200")]));
        await Service(db, first.Object, clock: clock).ImportAsync(OrgA, since: null);
        var stored = await db.FiscalDocuments.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal("ACC-A", stored.Single(d => d.OctorateDocumentId == "D-A").OctorateAccommodationId);
        Assert.Equal("ACC-B", stored.Single(d => d.OctorateDocumentId == "D-B").OctorateAccommodationId);

        // Ten days later B fails while A answers: A moves on, B keeps its date.
        clock.Advance(TimeSpan.FromDays(10));
        var second = new Mock<IOctorateApiClient>();
        second.Setup(c => c.GetDocumentsAsync("ACC-A", It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([Document("D-A", "R-100")]);
        second.Setup(c => c.GetDocumentsAsync("ACC-B", It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(500));
        Assert.Equal(1, (await Service(db, second.Object, clock: clock).ImportAsync(OrgA, since: null)).FailedReads);

        // The next run asks B from its own last import (less the overlap), not from the date A moved to.
        clock.Advance(TimeSpan.FromDays(1));
        var third = Client(("ACC-A", [Document("D-A", "R-100")]), ("ACC-B", [Document("D-B", "R-200")]));
        await Service(db, third.Object, clock: clock).ImportAsync(OrgA, since: null);
        third.Verify(c => c.GetDocumentsAsync("ACC-B", Now.UtcDateTime - OctorateDocumentImport.Overlap, It.IsAny<CancellationToken>()), Times.Once);
        third.Verify(c => c.GetDocumentsAsync("ACC-A", Now.UtcDateTime.AddDays(10) - OctorateDocumentImport.Overlap, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("SDI_REJECTED", FiscalDocumentSdiStatuses.Rejected)]
    [InlineData("REJECTED_BEFORE", FiscalDocumentSdiStatuses.Rejected)]
    [InlineData("BEFORE_SDI", FiscalDocumentSdiStatuses.ToSend)]
    [InlineData("SDI_PROCESSING", FiscalDocumentSdiStatuses.Submitted)]
    [InlineData("DONE", FiscalDocumentSdiStatuses.Delivered)]
    [InlineData("EMPTY", FiscalDocumentSdiStatuses.Unknown)]
    [InlineData(null, FiscalDocumentSdiStatuses.Unknown)]
    public void SdiStatus_OfTheGroupOfOctorate(string? group, string expected) =>
        Assert.Equal(expected, OctorateDocumentImportService.SdiStatusOf(group));

    [Fact]
    public void DocumentOf_ReadsTheMembersOfTheOpenApi_AndLeavesOutAnItemWithoutId()
    {
        using var json = JsonDocument.Parse(
            """{"id":"771","refer":"R-1","type":"CREDIT_NOTE","number":"NC/3","date":"2026-10-30","total":"120.50","paid":true,"fiscalInvoiceGroup":"done","pdf":true}""");
        var document = OctorateApiClient.DocumentOf(json.RootElement)!;

        Assert.Equal("771", document.DocumentId);
        Assert.Equal("R-1", document.ReservationId);
        Assert.Equal(OctorateFiscalDocumentType.CreditNote, document.Type);
        Assert.Equal(new DateOnly(2026, 10, 30), document.Date);
        Assert.Equal(120.50m, document.Total);
        Assert.Equal("DONE", document.SdiGroup);
        Assert.True(document.PdfAvailable);
        Assert.Null(document.TaxableBase);

        using var noId = JsonDocument.Parse("""{"type":"INVOICE"}""");
        Assert.Null(OctorateApiClient.DocumentOf(noId.RootElement));
        Assert.Null(OctorateDocumentImportService.KindOf(OctorateFiscalDocumentType.SummaryDocument));
    }

    [Fact]
    public void Job_IsScheduledEveryThirtyMinutesOnlyWithBothFlags_AndNeverRunsTwiceAtOnce()
    {
        var registered = new Dictionary<string, (Hangfire.Common.Job Job, string Cron)>();
        var manager = new Mock<Hangfire.IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Hangfire.Common.Job>(), It.IsAny<string>(), It.IsAny<Hangfire.RecurringJobOptions>()))
            .Callback<string, Hangfire.Common.Job, string, Hangfire.RecurringJobOptions>((id, job, cron, _) => registered[id] = (job, cron));

        Casazen.Web.BackgroundJobs.RecurringJobsRegistration.Configure(manager.Object, new TestFeatureFlags(FeatureFlags.OctorateConnector));
        Assert.DoesNotContain(Casazen.Web.BackgroundJobs.OctorateDocumentImportJob.RecurringJobId, registered.Keys);
        manager.Verify(m => m.RemoveIfExists(Casazen.Web.BackgroundJobs.OctorateDocumentImportJob.RecurringJobId), Times.Once);

        Casazen.Web.BackgroundJobs.RecurringJobsRegistration.Configure(
            manager.Object, new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.OctorateDocumentImport));
        var (job, cron) = registered["octorate-document-import"];
        Assert.Equal("*/30 * * * *", cron);
        Assert.NotNull(job.Method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), false).SingleOrDefault());
    }

    private static OctorateFiscalDocument Document(string id, string reservation) =>
        new(id, reservation, OctorateFiscalDocumentType.Invoice, $"F/{id}", new DateOnly(2026, 10, 30), 100m, 10m, 110m, "EUR", true, "DONE", true, "TEST Ospite");

    private static Mock<IOctorateApiClient> Client(params (string Accommodation, OctorateFiscalDocument[] Documents)[] answers)
    {
        var client = new Mock<IOctorateApiClient>();
        foreach (var (accommodation, documents) in answers)
        {
            client.Setup(c => c.GetDocumentsAsync(accommodation, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(documents);
        }

        return client;
    }

    private static Mock<IInternalActivityService> Activities()
    {
        var activities = new Mock<IInternalActivityService>();
        activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        return activities;
    }

    private static OctorateDocumentImportService Service(
        AppDbContext db,
        IOctorateApiClient client,
        IInternalActivityService? activities = null,
        IFeatureFlags? flags = null,
        FakeTimeProvider? clock = null,
        bool importOctorateDocuments = true)
    {
        var scope = new Mock<IOctorateOrgScope>();
        scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        return new OctorateDocumentImportService(
            db,
            client,
            scope.Object,
            activities ?? Activities().Object,
            flags ?? new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.OctorateDocumentImport),
            Microsoft.Extensions.Options.Options.Create(new Casazen.Core.Options.FiscalDocumentRulesOptions { ImportOctorateDocuments = importOctorateDocuments }),
            environment.Object,
            clock ?? new FakeTimeProvider(Now),
            NullLogger<OctorateDocumentImportService>.Instance);
    }

    /// <summary>One more booking of a reservation, on the first property of the org.</summary>
    private static async Task<Guid> AddBookingAsync(AppDbContext db, Guid orgId, string reservation)
    {
        var first = await db.Bookings.IgnoreQueryFilters().FirstAsync(b => b.OrgId == orgId);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = first.PropertyId,
            GuestId = first.GuestId,
            CheckInDate = new DateTime(2026, 10, 24),
            CheckOutDate = new DateTime(2026, 10, 26),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Airbnb,
            OctorateReservationId = reservation,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>A second structure of an org that is already connected: its mapping, its property and a booking of one reservation.</summary>
    private static async Task AddStructureAsync(AppDbContext db, Guid orgId, string accommodation, string reservation)
    {
        var connection = await db.OctorateConnections.IgnoreQueryFilters().FirstAsync(c => c.OrgId == orgId);
        var property = new Property { OrgId = orgId, OwnerId = "owner", Name = "TEST Casa 2", Address = "Via 2", City = "Roma", Currency = "EUR" };
        var guest = new Guest { OrgId = orgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = orgId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = accommodation,
            OctorateProductId = "P-2",
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 20),
            CheckOutDate = new DateTime(2026, 10, 23),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.BookingCom,
            OctorateReservationId = reservation,
        });
        await db.SaveChangesAsync();
    }

    private static string? _name;

    private static AppDbContext NewDb()
    {
        _name = $"OctorateDocuments_{Guid.NewGuid()}";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options, new DocumentTenant(null), new EphemeralDataProtectionProvider());
    }

    private static AppDbContext NewDb(AppDbContext same, Guid orgId) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name!).Options, new DocumentTenant(orgId), new EphemeralDataProtectionProvider());

    private static async Task<(Guid PropertyId, Guid BookingId)> SeedOrgAsync(AppDbContext db, Guid orgId, string accommodation, string reservation)
    {
        var connection = new OctorateConnection { OrgId = orgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        var property = new Property { OrgId = orgId, OwnerId = "owner", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        var guest = new Guest { OrgId = orgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 20),
            CheckOutDate = new DateTime(2026, 10, 23),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.BookingCom,
            OctorateReservationId = reservation,
        };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = orgId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = accommodation,
            OctorateProductId = "P-1",
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = orgId, IssuerMode = DocumentIssuerMode.CasaZen });
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (property.Id, booking.Id);
    }
}

file sealed class DocumentTenant(Guid? orgId) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => orgId is not null;
}
