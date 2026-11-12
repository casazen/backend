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
/// E06/F03/US02/T01 (NF-84, FIS-04): the documents Octorate issued are imported only into the org of the connection and linked only to its own
/// bookings, their rejected SDI state and their amount to collect become "To do" items that close when the state changes, a booking CasaZen already
/// issued for raises <c>two_issuers_detected</c> instead of hiding the document, and nothing is read while the import is off.
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

    [Fact]
    public async Task Import_BookingCasazenIssuedFor_StoresTheDocumentAndRaisesTwoIssuers()
    {
        await using var db = NewDb();
        var (_, booking) = await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var issuers = new Mock<IDocumentIssuerService>();
        issuers.Setup(i => i.LockIssuerAsync(booking, BookingDocumentIssuerKind.Octorate, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException(DocumentIssuerErrorCodes.IssuerConflict, "DocumentIssuerConflict"));
        var activities = Activities();

        var result = await Service(db, Client(("ACC-A", [Document("D-3", "R-100")])).Object, issuers.Object, activities.Object).ImportAsync(OrgA, since: null);

        Assert.Equal(1, result.TwoIssuers);
        Assert.Single(await db.FiscalDocuments.IgnoreQueryFilters().ToListAsync());
        activities.Verify(a => a.CreateAsync(
            It.Is<CreateTodoRequest>(r => r.Reason == OctorateDocumentImport.TwoIssuersReason && r.EntityId == booking && r.Urgency == ActivityUrgency.High),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_OffOrNotOctorateIssuer_ReadsNothing_AndAFailedReadIsCountedNotThrown()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, OrgA, "ACC-A", "R-100");
        var client = Client(("ACC-A", [Document("D-1", "R-100")]));

        var off = Service(db, client.Object, flags: new TestFeatureFlags(FeatureFlags.OctorateConnector));
        Assert.Empty(await off.OrgsToImportAsync());
        Assert.Equal(0, (await off.ImportAsync(OrgA, since: null)).Created);
        client.Verify(c => c.GetDocumentsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(new[] { OrgA }, await Service(db, client.Object).OrgsToImportAsync());

        var failing = new Mock<IOctorateApiClient>();
        failing.Setup(c => c.GetDocumentsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        var result = await Service(db, failing.Object).ImportAsync(OrgA, since: null);
        Assert.Equal(1, result.FailedReads);
        Assert.Empty(await db.FiscalDocuments.IgnoreQueryFilters().ToListAsync());
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
        IDocumentIssuerService? issuers = null,
        IInternalActivityService? activities = null,
        IFeatureFlags? flags = null)
    {
        var scope = new Mock<IOctorateOrgScope>();
        scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        return new OctorateDocumentImportService(
            db,
            client,
            scope.Object,
            issuers ?? Mock.Of<IDocumentIssuerService>(),
            activities ?? Activities().Object,
            flags ?? new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.OctorateDocumentImport),
            environment.Object,
            new FakeTimeProvider(Now),
            NullLogger<OctorateDocumentImportService>.Instance);
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
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = orgId, IssuerMode = DocumentIssuerMode.Octorate });
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
