using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using RuleKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F01/US01/T01 (NF-83, NF-82, NF-84): the documents CasaZen issues for a direct booking are cent-accurate, never issued for an OTA booking or a
/// booking locked to Octorate, immutable once issued, credited never above the original, and the documents imported from Octorate are read-only.
/// </summary>
public class FiscalDocumentServiceProofTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FiscalDocument_Totals_CentAccurate_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var service = Service(db);

        var draft = await service.CreateDraftAsync(booking, "auth0|host");

        Assert.Equal(FiscalDocumentStatus.Draft, draft.Status);
        Assert.Null(draft.Number);
        Assert.Equal(DocumentKind.Invoice, draft.Kind);
        Assert.Equal(330.18m, draft.TaxableBase);
        Assert.Equal(31.82m, draft.VatAmount);
        Assert.Equal(0m, draft.StampDuty);
        Assert.Equal(362.00m, draft.Total);
        Assert.Equal(3, draft.Lines.Count);
        Assert.Equal(272.73m, draft.Lines.Single(l => l.LineNumber == 1).TotalPrice);
        Assert.Equal("N1", draft.Lines.Single(l => l.LineNumber == 3).VatNatureCode);

        var issued = await service.IssueAsync(draft.Id, confirm: true);

        Assert.Equal(FiscalDocumentStatus.Issued, issued.Status);
        Assert.Equal("2026/1", issued.Number);
        Assert.Equal(new DateOnly(2026, 11, 12), issued.IssueDate);
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, issued.SdiStatus);
        Assert.Equal(362.00m, issued.Total);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, (await db.BookingDocumentIssuers.SingleAsync()).Issuer);
    }

    [Theory]
    [InlineData(BookingSource.BookingCom)]
    [InlineData(BookingSource.Airbnb)]
    [InlineData(BookingSource.OtherChannel)]
    public async Task FiscalDocument_OtaBooking_NeverIssuedByCasazen_Test(BookingSource source)
    {
        await using var db = NewDb();
        var ota = await SeedAsync(db, source);
        var direct = await SeedBookingAsync(db, BookingSource.Direct);
        db.BookingDocumentIssuers.Add(new BookingDocumentIssuer { OrgId = OrgId, BookingId = direct, Issuer = BookingDocumentIssuerKind.Octorate, LockedAt = Now.UtcDateTime });
        await db.SaveChangesAsync();
        var service = Service(db);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateDraftAsync(ota, null));
        Assert.Equal(FiscalDocumentErrors.BookingNotDirect, refused.Code);
        var locked = await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateDraftAsync(direct, null));
        Assert.Equal(FiscalDocumentErrors.IssuerConflict, locked.Code);

        var off = Service(db, flags: new TestFeatureFlags());
        var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(() => off.CreateDraftAsync(ota, null));
        Assert.Equal(FiscalDocumentErrors.BookingNotDirect, notAvailable.Code);
        Assert.Empty(await db.FiscalDocuments.ToListAsync());
    }

    [Fact]
    public async Task FiscalDocument_IssuedDocumentImmutable_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var service = Service(db);
        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);
        var before = Snapshot(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id));

        await Assert.ThrowsAsync<DomainRuleException>(() => service.RefreshDraftAsync(issued.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.DiscardDraftAsync(issued.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.IssueAsync(issued.Id, confirm: true));
        var again = await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.AlreadyIssued, again.Code);

        Assert.Equal(before, Snapshot(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id)));
    }

    [Fact]
    public async Task FiscalDocument_CreditNote_CannotExceedOriginal_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var service = Service(db);
        var original = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);

        var part = await service.CreateCreditNoteAsync(original.Id, 100m, "TEST rimborso parziale", confirm: true, null);
        Assert.Equal(100m, part.Total);
        Assert.Equal(DocumentKind.CreditNote, part.Kind);
        Assert.Equal(original.Id, part.CreditedDocumentId);
        Assert.Equal(original.Series, part.Series);
        Assert.Equal(part.TaxableBase + part.VatAmount, part.Total);

        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateCreditNoteAsync(original.Id, 262.01m, "TEST", confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.CreditAmountExceeds, tooMuch.Code);

        var rest = await service.CreateCreditNoteAsync(original.Id, null, "TEST storno del residuo", confirm: true, null);
        Assert.Equal(262m, rest.Total);
        var credited = await db.FiscalDocuments.Where(d => d.CreditedDocumentId == original.Id).SumAsync(d => d.Total);
        Assert.Equal(original.Total, credited);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateCreditNoteAsync(original.Id, 0.01m, "TEST", confirm: true, null));
    }

    [Fact]
    public async Task OctorateDocument_ReadOnlyInCasazen_Test()
    {
        await using var db = NewDb();
        await SeedAsync(db, BookingSource.Direct);
        var imported = new FiscalDocument
        {
            OrgId = OrgId,
            Source = FiscalDocumentSource.Octorate,
            Kind = DocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Number = "OCT/7",
            Currency = "EUR",
            Total = 120m,
            OctorateDocumentId = "D-7",
            SdiStatus = FiscalDocumentSdiStatuses.Unknown,
        };
        db.FiscalDocuments.Add(imported);
        await db.SaveChangesAsync();
        var service = Service(db);

        foreach (var change in new Func<Task>[]
                 {
                     () => service.RefreshDraftAsync(imported.Id),
                     () => service.DiscardDraftAsync(imported.Id),
                     () => service.IssueAsync(imported.Id, confirm: true),
                     () => service.CreateCreditNoteAsync(imported.Id, null, "TEST", confirm: true, null),
                     () => service.GetPdfAsync(imported.Id, "it"),
                 })
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(change);
            Assert.Equal(FiscalDocumentErrors.ReadOnly, error.Code);
        }

        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == imported.Id);
        Assert.Equal(FiscalDocumentStatus.Issued, stored.Status);
        Assert.Equal(120m, stored.Total);
        Assert.Single(await db.FiscalDocuments.ToListAsync());
    }

    [Fact]
    public async Task Issue_WithoutConfirmation_IsRefused_AndAReceiptHasNoXml()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Manual, regime: DocumentRegime.PrivatePerson);
        var service = Service(db);
        var draft = await service.CreateDraftAsync(booking, null);

        var unconfirmed = await Assert.ThrowsAsync<DomainRuleException>(() => service.IssueAsync(draft.Id, confirm: false));
        Assert.Equal(FiscalDocumentErrors.ConfirmationRequired, unconfirmed.Code);

        var receipt = await service.IssueAsync(draft.Id, confirm: true);
        Assert.Equal(DocumentKind.Receipt, receipt.Kind);
        Assert.Equal(FiscalDocumentSdiStatuses.NotApplicable, receipt.SdiStatus);
        Assert.Null(receipt.FatturaPaXml);
        Assert.Equal(0m, receipt.VatAmount);
        Assert.Equal(362.00m, receipt.Total);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetXmlAsync(receipt.Id));
    }

    private static string Snapshot(FiscalDocument d) =>
        string.Join('|', d.Number, d.Status, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.RecipientName, d.RecipientFiscalCode,
            string.Join(',', d.Lines.OrderBy(l => l.LineNumber).Select(l => $"{l.LineNumber}:{l.Description}:{l.TotalPrice}:{l.VatRatePercent}")));

    private static FiscalDocumentService Service(AppDbContext db, IFeatureFlags? flags = null)
    {
        var featureFlags = flags ?? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var options = Options.Create(Rules());
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var issuers = new DocumentIssuerService(db, featureFlags, options, environment.Object, new FakeTimeProvider(Now), NullLogger<DocumentIssuerService>.Instance);
        var invoiceData = new Mock<IInvoiceDataService>();
        invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => new InvoiceDataView(
                bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
                "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime));
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        return new FiscalDocumentService(
            db,
            issuers,
            invoiceData.Object,
            new FiscalNumberingService(db, new FakeTimeProvider(Now), NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            options,
            new FakeTimeProvider(Now),
            NullLogger<FiscalDocumentService>.Instance);
    }

    private static FiscalDocumentRulesOptions Rules() => new()
    {
        Rules =
        {
            [DocumentRegime.VatOrdinary] = new FiscalDocumentRule
            {
                DocumentKind = RuleKind.Invoice,
                FatturaPaRegimeCode = "RF01",
                VatRatePercent = 10m,
                TouristTaxNatureCode = "N1",
            },
            [DocumentRegime.PrivatePerson] = new FiscalDocumentRule { DocumentKind = RuleKind.Receipt },
        },
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalDocuments_{Guid.NewGuid()}").Options, new ServiceTenant(), new EphemeralDataProtectionProvider());

    private static async Task<Guid> SeedAsync(AppDbContext db, BookingSource source, DocumentRegime regime = DocumentRegime.VatOrdinary)
    {
        db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = "TEST Casa Vacanze",
            Slug = $"test-{OrgId:N}"[..20],
            DisplayName = "TEST Casa Vacanze",
            ContactEmail = "test@example.com",
            HasPartitaIva = true,
            PartitaIvaNumber = "01234567897",
            FiscalCode = "01234567897",
        });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            IssuerMode = DocumentIssuerMode.CasaZenDirectOnly,
            DocumentRegime = regime,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
        });
        await db.SaveChangesAsync();
        return await SeedBookingAsync(db, source);
    }

    private static async Task<Guid> SeedBookingAsync(AppDbContext db, BookingSource source)
    {
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 1),
            CheckOutDate = new DateTime(2026, 11, 4),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = source,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TotalPrice = 362m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }
}

file sealed class ServiceTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
