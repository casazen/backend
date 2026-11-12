using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-83, NF-84 (E06/F01/US02/T01) on a real PostgreSQL database, where the rules live: the database refuses a second document with the same number
/// in one org and series and a second import of one Octorate document, the recipient snapshot and the XML are stored encrypted, and an issued
/// document survives the erasure of the guest and the removal of the booking (legal retention of ten years, ADR-033-R7).
/// </summary>
public class FiscalDocumentPostgresTests
{
    private const string RecipientName = "Trattoria Il Glicine S.r.l.";
    private const string RecipientFiscalCode = "GLCTRT80A01H501Z";
    private const string RecipientVatNumber = "IT01234567897";
    private const string Street = "Via dei Glicini Viola";
    private const string Pec = "fatture.glicine@pec.example.com";
    private const string Xml = "<FatturaElettronica><Denominazione>Trattoria Il Glicine S.r.l.</Denominazione></FatturaElettronica>";

    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>PROOF TEST: money and data rule. No number twice in one org and series, no Octorate document imported twice.</summary>
    [PostgresFact]
    public async Task FiscalDocument_NumberUniquePerSeries_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdnum");
        var orgId = Guid.NewGuid();
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(Issued(orgId, "2026", 1));
            db.FiscalDocuments.Add(Issued(orgId, "2026", 2));
            db.FiscalDocuments.Add(Issued(orgId, "B", 1));
            db.FiscalDocuments.Add(Issued(Guid.NewGuid(), "2026", 1));
            db.FiscalDocuments.Add(Imported(orgId, "OCT-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(Issued(orgId, "2026", 2));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(FiscalDocument.SeriesNumberIndexName, duplicate.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
        }

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(Imported(orgId, "OCT-1"));
            var twice = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(FiscalDocument.OctorateDocumentIndexName, twice.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
        }

        // Drafts have no number: any number of them in the same series.
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(new FiscalDocument { OrgId = orgId, Series = "2026", Currency = "EUR" });
            db.FiscalDocuments.Add(new FiscalDocument { OrgId = orgId, Series = "2026", Currency = "EUR" });
            await db.SaveChangesAsync();
            Assert.Equal(7, await db.FiscalDocuments.CountAsync());
        }
    }

    /// <summary>PROOF TEST: privacy rule. The raw text of every recipient column and of the XML holds a payload and none of the value.</summary>
    [PostgresFact]
    public async Task FiscalDocument_RecipientData_EncryptedAtRest_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdenc");
        var orgId = Guid.NewGuid();
        var document = Issued(orgId, "2026", 1);
        document.RecipientKind = InvoiceRecipientKind.Company;
        document.RecipientName = RecipientName;
        document.RecipientFiscalCode = RecipientFiscalCode;
        document.RecipientVatNumber = RecipientVatNumber;
        document.RecipientAddressStreet = Street;
        document.RecipientAddressCivic = "Scala 7/C";
        document.RecipientPostalCode = "00187";
        document.RecipientCity = "Roma";
        document.RecipientProvince = "RM";
        document.RecipientCountryCode = "IT";
        document.RecipientSdiCode = "XYZ9876";
        document.RecipientPec = Pec;
        document.FatturaPaXml = Xml;

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(document);
            await db.SaveChangesAsync();
        }

        await using var stored = database.CreateContext();
        foreach (var column in EncryptedColumns.All.Where(c => c.EntityType == typeof(FiscalDocument)).Select(c => c.Property))
        {
            // The column names come from EncryptedColumns, never from input.
            var sql = "SELECT \"" + column + "\" AS \"Value\" FROM \"FiscalDocuments\"";
            var raw = await stored.Database.SqlQueryRaw<string>(sql).SingleAsync();
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, raw, StringComparison.Ordinal);
            foreach (var secret in new[] { RecipientName, "Glicine", RecipientFiscalCode, "01234567897", Street, Pec, "<FatturaElettronica>" })
                Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }

        await using var read = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        var back = await read.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(RecipientName, back.RecipientName);
        Assert.Equal(Pec, back.RecipientPec);
        Assert.Equal(Xml, back.FatturaPaXml);
    }

    /// <summary>
    /// PROOF TEST: data rule. The guest of the booking is erased (art. 17, through the real service) and then the booking is removed: the issued
    /// document stays with its recipient snapshot, and the database refuses the removal of a booking that has one (restrict, no cascade).
    /// </summary>
    [PostgresFact]
    public async Task FiscalDocument_SurvivesGuestErasureAndBookingRemoval_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdkeep");
        var orgId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var at = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var document = Issued(orgId, "2026", 1);
        document.BookingId = bookingId;
        document.RecipientName = RecipientName;
        document.RecipientFiscalCode = RecipientFiscalCode;

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            var propertyId = Guid.NewGuid();
            db.Orgs.Add(new OrgEntity
            {
                Id = orgId,
                Name = "Casa Documenti",
                Slug = $"fd-keep-{orgId:N}"[..28],
                DisplayName = "Casa Documenti",
                ContactEmail = "fd-keep@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            });
            db.Properties.Add(new Property { Id = propertyId, OrgId = orgId, OwnerId = "auth0|owner", Name = "Casa del Glicine", Address = "Via 1", City = "Roma" });
            db.Guests.Add(new Guest { Id = guestId, OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"anna.{guestId:N}@example.com" });
            db.Bookings.Add(new Booking
            {
                Id = bookingId,
                OrgId = orgId,
                PropertyId = propertyId,
                GuestId = guestId,
                CheckInDate = at.AddDays(-10),
                CheckOutDate = at.AddDays(-7),
                NumberOfGuests = 1,
                Status = BookingStatus.CheckedOut,
                Source = BookingSource.Direct,
                BasePrice = 300m,
                TotalPrice = 300m,
            });
            db.FiscalDocuments.Add(document);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            var repository = new Mock<IGuestRepository>();
            repository
                .Setup(r => r.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuestUsage(HasReferences: true, HasOpenBookings: false));
            var gdpr = new GdprService(
                db,
                repository.Object,
                new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
                Options.Create(new GdprOptions()),
                TimeProvider.System,
                NullLogger<GdprService>.Instance);
            await gdpr.EraseGuestDataAsync(orgId, guestId, "test erasure", actorUserId: null);
        }

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            db.Bookings.Remove(booking);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var check = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        var kept = await check.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(FiscalDocumentStatus.Issued, kept.Status);
        Assert.Equal(bookingId, kept.BookingId);
        Assert.Equal(RecipientName, kept.RecipientName);
        Assert.Equal(RecipientFiscalCode, kept.RecipientFiscalCode);
        Assert.Equal(122m, kept.Total);
        Assert.True(await check.Bookings.AnyAsync(b => b.Id == bookingId));
    }

    private static FiscalDocument Issued(Guid orgId, string series, int number) => new()
    {
        OrgId = orgId,
        Source = FiscalDocumentSource.CasaZen,
        Kind = DocumentKind.Invoice,
        Status = FiscalDocumentStatus.Issued,
        Series = series,
        SequenceNumber = number,
        Number = $"{series}/{number}",
        IssueDate = new DateOnly(2026, 10, 2),
        Currency = "EUR",
        TaxableBase = 100m,
        VatAmount = 22m,
        Total = 122m,
        VatRatePercent = 22m,
        SdiStatus = FiscalDocumentSdiStatuses.ToSend,
    };

    private static FiscalDocument Imported(Guid orgId, string octorateId) => new()
    {
        OrgId = orgId,
        Source = FiscalDocumentSource.Octorate,
        Kind = DocumentKind.Receipt,
        Status = FiscalDocumentStatus.Issued,
        Number = "R-77",
        Currency = "EUR",
        Total = 80m,
        OctorateDocumentId = octorateId,
        SdiStatus = FiscalDocumentSdiStatuses.Unknown,
    };
}
