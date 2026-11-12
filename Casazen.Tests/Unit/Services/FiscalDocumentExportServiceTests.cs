using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F03/US03/T01 (FIS-07, NF-87): the rows of the exported list are the ones the list shows, narrowed in SQL to the scope of the caller (a caller who
/// reaches some properties never gets a document of another one, nor one without a booking, nor one of another org), only the documents that were issued,
/// with the current payment state and the booking and the property of each.
/// </summary>
public class FiscalDocumentExportServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Anna = "auth0|anna-collaborator";

    /// <summary>Permission rule: a caller with a partial scope never gets the documents of the properties it does not reach, nor the ones of another org.</summary>
    [Fact]
    public async Task DocumentExport_HostScope_OtherPropertiesNeverIncluded_Test()
    {
        await using var db = NewDb();
        var (granted, grantedProperty) = await SeedBookingAsync(db, OrgId, "TEST Casa assegnata");
        var (hidden, hiddenProperty) = await SeedBookingAsync(db, OrgId, "TEST Casa nascosta");
        var (foreign, _) = await SeedBookingAsync(db, OtherOrgId, "TEST Casa di un'altra organizzazione");
        var mine = Document(OrgId, granted, "2026/1");
        var notMine = Document(OrgId, hidden, "2026/2");
        var withoutBooking = Document(OrgId, null, "OCT/7", source: FiscalDocumentSource.Octorate);
        var ofAnotherOrg = Document(OtherOrgId, foreign, "2026/1");
        db.FiscalDocuments.AddRange(mine, notMine, withoutBooking, ofAnotherOrg);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = OrgId, UserId = Anna, PropertyId = grantedProperty });
        await db.SaveChangesAsync();
        var service = Service(db);
        var collaborator = new HostScope(OrgId, GrantedToUserId: Anna);

        var restricted = await service.ListForExportAsync(collaborator, new FiscalDocumentCriteria(), 100);

        Assert.Equal([mine.Id], restricted.Select(r => r.Id));

        // Asking for the other property or booking by name does not widen what the caller reaches.
        Assert.Empty(await service.ListForExportAsync(collaborator, new FiscalDocumentCriteria(PropertyId: hiddenProperty), 100));
        Assert.Empty(await service.ListForExportAsync(collaborator, new FiscalDocumentCriteria(BookingId: hidden), 100));

        // The whole org: its three documents, the one without a booking included, never the one of another org.
        var whole = await service.ListForExportAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 100);
        Assert.Equal(3, whole.Count);
        Assert.Contains(whole, r => r.Id == withoutBooking.Id);
        Assert.DoesNotContain(whole, r => r.Id == ofAnotherOrg.Id);
    }

    /// <summary>A draft has no number and no day, and a discarded one is no document: only what was issued is exported, whatever the criteria say.</summary>
    [Fact]
    public async Task DocumentExport_OnlyIssuedDocuments_WhateverTheStatusAskedFor()
    {
        await using var db = NewDb();
        var (booking, _) = await SeedBookingAsync(db, OrgId, "TEST Casa");
        var issued = Document(OrgId, booking, "2026/1");
        var draft = Document(OrgId, booking, null, status: FiscalDocumentStatus.Draft, issuedOn: null);
        var discarded = Document(OrgId, booking, null, status: FiscalDocumentStatus.Discarded, issuedOn: null);
        db.FiscalDocuments.AddRange(issued, draft, discarded);
        await db.SaveChangesAsync();
        var service = Service(db);

        foreach (var status in new FiscalDocumentStatus?[] { null, FiscalDocumentStatus.Draft, FiscalDocumentStatus.Discarded, FiscalDocumentStatus.Issued })
        {
            var rows = await service.ListForExportAsync(new HostScope(OrgId), new FiscalDocumentCriteria(Status: status), 100);

            Assert.Equal([issued.Id], rows.Select(r => r.Id));
        }
    }

    /// <summary>Money rule: the payment state of a document CasaZen issued is the current one, as in the list; a credit note and an imported document keep their own.</summary>
    [Fact]
    public async Task DocumentExport_PaymentState_IsTheCurrentOne_AsInTheList()
    {
        await using var db = NewDb();
        var (paidBooking, _) = await SeedBookingAsync(db, OrgId, "TEST Casa 1");
        var (refundedBooking, _) = await SeedBookingAsync(db, OrgId, "TEST Casa 2");
        var (unpaidBooking, _) = await SeedBookingAsync(db, OrgId, "TEST Casa 3");
        var paid = Document(OrgId, paidBooking, "2026/1");
        var refunded = Document(OrgId, refundedBooking, "2026/2");
        var unpaid = Document(OrgId, unpaidBooking, "2026/3");
        var creditNote = Document(OrgId, paidBooking, "2026/4", kind: DocumentKind.CreditNote, paymentState: FiscalDocumentPaymentState.Unknown);
        var imported = Document(OrgId, paidBooking, "OCT/9", source: FiscalDocumentSource.Octorate, paymentState: FiscalDocumentPaymentState.ToCollect);
        db.FiscalDocuments.AddRange(paid, refunded, unpaid, creditNote, imported);
        db.Payments.AddRange(
            new Payment { OrgId = OrgId, BookingId = paidBooking, Amount = 362m, Status = PaymentStatus.Completed, ProcessedAt = Now.UtcDateTime },
            new Payment { OrgId = OrgId, BookingId = refundedBooking, Amount = 362m, RefundedAmount = 100m, Status = PaymentStatus.PartiallyRefunded, ProcessedAt = Now.UtcDateTime });
        await db.SaveChangesAsync();
        var service = Service(db);

        var rows = (await service.ListForExportAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 100)).ToDictionary(r => r.Id);

        Assert.Equal(FiscalDocumentPaymentState.Paid, rows[paid.Id].PaymentState);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, rows[refunded.Id].PaymentState);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, rows[unpaid.Id].PaymentState);
        Assert.Equal(FiscalDocumentPaymentState.Unknown, rows[creditNote.Id].PaymentState);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, rows[imported.Id].PaymentState);

        // The list says the same of the same documents.
        var listed = (await service.ListAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 1, 100)).Items;
        Assert.All(listed, document => Assert.Equal(document.PaymentState, rows[document.Id].PaymentState));
    }

    [Fact]
    public async Task DocumentExport_CarriesTheBookingCodeThePropertyAndTheStoredAmounts_EvenOfADeletedProperty()
    {
        await using var db = NewDb();
        var (booking, property) = await SeedBookingAsync(db, OrgId, "TEST Casa che c'era");
        var document = Document(OrgId, booking, "2026/1");
        document.TaxableBase = 330.18m;
        document.VatAmount = 31.82m;
        document.StampDuty = 2m;
        document.Total = 364m;
        document.Currency = "CHF";
        document.SdiStatus = FiscalDocumentSdiStatuses.Delivered;
        db.FiscalDocuments.Add(document);
        await db.SaveChangesAsync();
        (await db.Properties.SingleAsync(p => p.Id == property)).IsDeleted = true;
        await db.SaveChangesAsync();

        var row = Assert.Single(await Service(db).ListForExportAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 100));

        Assert.Equal((await db.Bookings.SingleAsync(b => b.Id == booking)).BookingCode, row.BookingCode);
        Assert.Equal("TEST Casa che c'era", row.PropertyName);
        Assert.Equal("TEST Cliente Srl", row.RecipientName);
        Assert.Equal((330.18m, 31.82m, 2m, 364m, "CHF"), (row.TaxableBase, row.VatAmount, row.StampDuty, row.Total, row.Currency));
        Assert.Equal(FiscalDocumentSdiStatuses.Delivered, row.SdiStatus);
        Assert.Equal(new DateOnly(2026, 11, 12), row.IssueDate);
    }

    /// <summary>The export narrows like the list (period in Rome days, kind, source, booking, property), newest first, and stops at the limit.</summary>
    [Fact]
    public async Task DocumentExport_FiltersLikeTheList_NewestFirst_AndStopsAtTheLimit()
    {
        await using var db = NewDb();
        var (booking, property) = await SeedBookingAsync(db, OrgId, "TEST Casa");
        var (other, otherProperty) = await SeedBookingAsync(db, OrgId, "TEST Altra casa");
        var october = Document(OrgId, booking, "2026/1", issuedOn: new DateOnly(2026, 10, 5));
        var credit = Document(OrgId, booking, "2026/2", kind: DocumentKind.CreditNote, issuedOn: new DateOnly(2026, 10, 20));
        var imported = Document(OrgId, other, "OCT/3", source: FiscalDocumentSource.Octorate, issuedOn: new DateOnly(2026, 11, 2));
        var receipt = Document(OrgId, other, "2026/4", kind: DocumentKind.Receipt, issuedOn: new DateOnly(2026, 11, 10));
        db.FiscalDocuments.AddRange(october, credit, imported, receipt);
        await db.SaveChangesAsync();
        var service = Service(db);
        var scope = new HostScope(OrgId);

        Assert.Equal([receipt.Id, imported.Id, credit.Id, october.Id], (await service.ListForExportAsync(scope, new FiscalDocumentCriteria(), 100)).Select(r => r.Id));
        Assert.Equal([receipt.Id, imported.Id], (await service.ListForExportAsync(scope, new FiscalDocumentCriteria(), 2)).Select(r => r.Id));

        var criteria = new[]
        {
            new FiscalDocumentCriteria(From: new DateOnly(2026, 10, 15), To: new DateOnly(2026, 11, 5)),
            new FiscalDocumentCriteria(From: new DateOnly(2026, 11, 2)),
            new FiscalDocumentCriteria(To: new DateOnly(2026, 10, 20)),
            new FiscalDocumentCriteria(Kind: DocumentKind.CreditNote),
            new FiscalDocumentCriteria(Source: FiscalDocumentSource.Octorate),
            new FiscalDocumentCriteria(BookingId: other),
            new FiscalDocumentCriteria(PropertyId: property),
            new FiscalDocumentCriteria(PropertyId: otherProperty, Kind: DocumentKind.Receipt),
        };
        foreach (var one in criteria)
        {
            var exported = (await service.ListForExportAsync(scope, one, 100)).Select(r => r.Id).ToList();
            var listed = (await service.ListAsync(scope, one with { Status = FiscalDocumentStatus.Issued }, 1, 100)).Items.Select(d => d.Id).ToList();

            Assert.Equal(listed, exported);
        }

        Assert.Equal([imported.Id, credit.Id], (await service.ListForExportAsync(scope, criteria[0], 100)).Select(r => r.Id));
        Assert.Equal([credit.Id], (await service.ListForExportAsync(scope, criteria[3], 100)).Select(r => r.Id));
        Assert.Equal([receipt.Id], (await service.ListForExportAsync(scope, criteria[7], 100)).Select(r => r.Id));
    }

    [Fact]
    public async Task DocumentExport_WithNothingToExport_IsAnEmptyList()
    {
        await using var db = NewDb();

        Assert.Empty(await Service(db).ListForExportAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 100));
    }

    /// <summary>
    /// The first day of year one is the whole history, not an error: <c>GET api/fiscal/documents?from=0001-01-01</c> was a 500 because the start of that day in Rome is before
    /// the first instant a <see cref="DateTime"/> holds. The list and the export answer, for a document with a day and for one read by the day it was created.
    /// </summary>
    [Fact]
    public async Task DocumentList_AFirstDayOfYearOne_IsTheWholeHistory_AndNeverAnError()
    {
        await using var db = NewDb();
        var (booking, _) = await SeedBookingAsync(db, OrgId, "TEST Casa");
        var dated = Document(OrgId, booking, "2026/1", issuedOn: new DateOnly(2026, 10, 5));
        var undated = Document(OrgId, booking, "OCT/2", source: FiscalDocumentSource.Octorate, issuedOn: new DateOnly(2026, 11, 2));
        undated.IssueDate = null;
        db.FiscalDocuments.AddRange(dated, undated);
        await db.SaveChangesAsync();
        var service = Service(db);
        var scope = new HostScope(OrgId);

        foreach (var criteria in new[]
        {
            new FiscalDocumentCriteria(From: DateOnly.MinValue),
            new FiscalDocumentCriteria(From: DateOnly.MinValue, To: DateOnly.MaxValue),
        })
        {
            Assert.Equal(2, (await service.ListAsync(scope, criteria, 1, 20)).TotalCount);
            Assert.Equal(2, (await service.ListForExportAsync(scope, criteria, 100)).Count);
        }

        // A first day that is a day of the list still narrows it by the day asked for, not by the one the start of the list is clamped to.
        Assert.Equal([dated.Id], (await service.ListAsync(scope, new FiscalDocumentCriteria(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31)), 1, 20)).Items.Select(d => d.Id));
    }

    private static FiscalDocumentService Service(AppDbContext db) => new(
        db,
        Mock.Of<IDocumentIssuerService>(),
        Mock.Of<IInvoiceDataService>(),
        Mock.Of<IFiscalNumberingService>(),
        Mock.Of<IFatturaPaXmlBuilder>(),
        Mock.Of<IDomainEventRecorder>(),
        Mock.Of<IPdfDocumentRenderer>(),
        new ResxDocumentTexts(),
        Options.Create(new FiscalDocumentRulesOptions()),
        new FakeTimeProvider(Now),
        NullLogger<FiscalDocumentService>.Instance);

    private static FiscalDocument Document(
        Guid orgId,
        Guid? bookingId,
        string? number,
        DocumentKind kind = DocumentKind.Invoice,
        FiscalDocumentSource source = FiscalDocumentSource.CasaZen,
        FiscalDocumentStatus status = FiscalDocumentStatus.Issued,
        DateOnly? issuedOn = null,
        FiscalDocumentPaymentState paymentState = FiscalDocumentPaymentState.ToCollect) => new()
        {
            OrgId = orgId,
            BookingId = bookingId,
            Source = source,
            Kind = kind,
            Status = status,
            Series = source == FiscalDocumentSource.CasaZen ? "2026" : string.Empty,
            Number = number,
            IssueDate = status == FiscalDocumentStatus.Issued ? issuedOn ?? new DateOnly(2026, 11, 12) : null,
            Currency = "EUR",
            RecipientName = "TEST Cliente Srl",
            TaxableBase = 330.18m,
            VatAmount = 31.82m,
            Total = 362m,
            PaymentState = paymentState,
            SdiStatus = FiscalDocumentSdiStatuses.ToSend,
            CreatedAt = (issuedOn ?? new DateOnly(2026, 11, 12)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalDocumentExport_{Guid.NewGuid()}").Options, new ExportTenant(), new EphemeralDataProtectionProvider());

    private static async Task<(Guid Booking, Guid Property)> SeedBookingAsync(AppDbContext db, Guid orgId, string propertyName)
    {
        var property = new Property { OrgId = orgId, OwnerId = "auth0|owner", Name = propertyName, Address = "Via 1", City = "Roma", Currency = "EUR" };
        var guest = new Guest { OrgId = orgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 1),
            CheckOutDate = new DateTime(2026, 11, 4),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            CleaningFee = 50m,
            TotalPrice = 362m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (booking.Id, property.Id);
    }
}

file sealed class ExportTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
