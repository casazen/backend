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
    private const string ClosingNote = "Nota di credito 4711 di Booking.com per il signor Mario Rossi";

    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>PROOF TEST: money and data rule. No number twice in one org and series, no Octorate document imported twice.</summary>
    [PostgresFact]
    public async Task FiscalDocument_NumberUniquePerSeries_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdnum");
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();

        // The numbers are per org, issuer and series (E17/F01/US01/T01): the issuer of each document is the profile of its organization here.
        var issuer = new OrgFiscalIssuerProfile { OrgId = orgId };
        var otherIssuer = new OrgFiscalIssuerProfile { OrgId = otherOrgId };
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.OrgFiscalIssuerProfiles.AddRange(issuer, otherIssuer);
            db.FiscalDocuments.Add(Issued(orgId, "2026", 1, issuer.Id));
            db.FiscalDocuments.Add(Issued(orgId, "2026", 2, issuer.Id));
            db.FiscalDocuments.Add(Issued(orgId, "B", 1, issuer.Id));
            db.FiscalDocuments.Add(Issued(otherOrgId, "2026", 1, otherIssuer.Id));
            db.FiscalDocuments.Add(Imported(orgId, "OCT-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(Issued(orgId, "2026", 2, issuer.Id));
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

        // E11/F01/US01/T01: the snapshot of the issuer is encrypted like the recipient (a column of the same list).
        document.IssuerSnapshot = new FiscalIssuerSnapshot { LegalName = RecipientName, AddressStreet = Street, VatNumber = "01234567897" }.ToJson();

        // FIX/BE-TD17-JOB-EXIT: the free note of the closing of a replacement as «non più dovuta» is the last column of the list. A closed document is all or nothing (a check of the table): discarded, with its
        // sequence number, its reason and its day.
        document.Status = FiscalDocumentStatus.Discarded;
        document.ClosedNotDueReason = SelfBillingCloseReasons.PortalCreditNote;
        document.ClosedNotDueAt = new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);
        document.ClosedNotDueNote = ClosingNote;

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

            // The payload is random text, so a short value (a single word, a postal code) turns up in it by chance: only values too long to be met by chance are searched for.
            foreach (var secret in new[] { RecipientName, RecipientFiscalCode, "01234567897", Street, Pec, "<FatturaElettronica>", ClosingNote })
                Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }

        await using var read = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        var back = await read.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(RecipientName, back.RecipientName);
        Assert.Equal(Pec, back.RecipientPec);
        Assert.Equal(Xml, back.FatturaPaXml);
        Assert.Equal(ClosingNote, back.ClosedNotDueNote);
    }

    /// <summary>
    /// PROOF TEST: money and data rule (FIX/BE-TD17-JOB-EXIT). The number of a replacement closed as «non più dovuta» stays reserved: the database refuses another document with the same series and number (the unique
    /// index, whatever the status of the first), and the closing is all or nothing (the check <c>CK_FiscalDocuments_ClosedNotDue</c>): a closed document that is not discarded, has lost its sequence number or has no reason,
    /// and a note, a user or a reason without a closing, are refused; a document that was never closed has none of the four columns and is accepted as it always was.
    /// </summary>
    [PostgresFact]
    public async Task FiscalDocument_AClosedNumberStaysReserved_AndTheClosingIsAllOrNothing_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdclosed");
        var orgId = Guid.NewGuid();
        var issuer = new OrgFiscalIssuerProfile { OrgId = orgId };
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.OrgFiscalIssuerProfiles.Add(issuer);
            db.FiscalDocuments.Add(Closed(orgId, issuer.Id, 1));
            db.FiscalDocuments.Add(Issued(orgId, "TD17-2026", 2, issuer.Id));
            await db.SaveChangesAsync();
        }

        // The number of the closed document is never taken again: another document of the same issuer and series with that number is refused by the unique index.
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            db.FiscalDocuments.Add(Issued(orgId, "TD17-2026", 1, issuer.Id));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(FiscalDocument.SeriesNumberIndexName, duplicate.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
        }

        // The closing is all or nothing: each of these shapes is refused by the check of the table.
        var shapes = new (string Name, Action<FiscalDocument> Change)[]
        {
            ("closed but not discarded", d => d.Status = FiscalDocumentStatus.Draft),
            ("closed but without its sequence number", d => d.SequenceNumber = null),
            ("closed but without its reason", d => d.ClosedNotDueReason = null),
            ("a note without a closing", d => (d.ClosedNotDueAt, d.ClosedNotDueReason) = (null, null)),
            ("a user without a closing", d => (d.ClosedNotDueAt, d.ClosedNotDueReason, d.ClosedNotDueNote) = (null, null, null)),
        };
        var number = 10;
        foreach (var (name, change) in shapes)
        {
            await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
            var document = Closed(orgId, issuer.Id, number++);
            change(document);
            db.FiscalDocuments.Add(document);
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(
                (refused.InnerException?.Message ?? string.Empty).Contains("CK_FiscalDocuments_ClosedNotDue", StringComparison.Ordinal),
                $"{name}: the check of the closing did not refuse it ({refused.InnerException?.Message})");
        }

        // What was accepted is what was written: the closed document and the one that was never closed.
        await using var read = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        var stored = await read.FiscalDocuments.AsNoTracking().OrderBy(d => d.SequenceNumber).ToListAsync();
        Assert.Equal(
            new[] { (FiscalDocumentStatus.Discarded, (int?)1, ClosingNote), (FiscalDocumentStatus.Issued, (int?)2, (string?)null) },
            stored.Select(d => (d.Status, d.SequenceNumber, d.ClosedNotDueNote)).ToArray());
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

    private static FiscalDocument Issued(Guid orgId, string series, int number, Guid? issuerProfileId = null) => new()
    {
        OrgId = orgId,
        IssuerProfileId = issuerProfileId,
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

    /// <summary>A replacement closed as «non più dovuta»: discarded, with its sequence number and its display number, its reason, its note, its user and its day.</summary>
    private static FiscalDocument Closed(Guid orgId, Guid issuerProfileId, int number)
    {
        var document = Issued(orgId, "TD17-2026", number, issuerProfileId);
        document.Purpose = FiscalDocumentPurpose.SelfBilling;
        document.Status = FiscalDocumentStatus.Discarded;
        document.IssueDate = null;
        document.SdiStatus = FiscalDocumentSdiStatuses.NotApplicable;
        document.ClosedNotDueReason = SelfBillingCloseReasons.PortalCreditNote;
        document.ClosedNotDueNote = ClosingNote;
        document.ClosedNotDueAt = new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);
        document.ClosedNotDueByUserId = "auth0|agency";
        return document;
    }

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
