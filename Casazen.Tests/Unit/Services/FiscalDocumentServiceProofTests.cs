using Casazen.Core.Authorization;
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
/// E06/F01/US01/T01, E11/F01/US01/T01 (NF-83, NF-82, NF-84, NF-112): the documents CasaZen issues for every active booking (the portals included) are
/// cent-accurate, never issued for a booking a legacy row locked to Octorate, immutable once issued (the issuer included: the snapshot), credited never
/// above the original, and the documents imported from Octorate are read-only.
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

    /// <summary>
    /// PROOF TEST: data rule (replaces «an OTA booking is never issued by CasaZen»). A booking of a portal imported through Octorate gets a CasaZen draft
    /// and is issued, with its number and the lock of CasaZen; a booking a legacy row locked to Octorate is still refused (it was issued there), and with
    /// the issuance off nothing is made.
    /// </summary>
    [Theory]
    [InlineData(BookingSource.BookingCom)]
    [InlineData(BookingSource.Airbnb)]
    [InlineData(BookingSource.OtherChannel)]
    public async Task FiscalDocument_PortalBooking_IsIssuedByCasazen_Test(BookingSource source)
    {
        await using var db = NewDb();
        var portal = await SeedAsync(db, source);
        var legacy = await SeedBookingAsync(db, BookingSource.Direct);
        db.BookingDocumentIssuers.Add(new BookingDocumentIssuer { OrgId = OrgId, BookingId = legacy, Issuer = BookingDocumentIssuerKind.Octorate, LockedAt = Now.UtcDateTime });
        await db.SaveChangesAsync();

        var off = Service(db, flags: new TestFeatureFlags());
        var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(() => off.CreateDraftAsync(portal, null));
        Assert.Equal(FiscalDocumentErrors.IssuanceNotAvailable, notAvailable.Code);
        Assert.Empty(await db.FiscalDocuments.ToListAsync());

        var service = Service(db);
        var locked = await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateDraftAsync(legacy, null));
        Assert.Equal(FiscalDocumentErrors.IssuerConflict, locked.Code);

        var draft = await service.CreateDraftAsync(portal, "auth0|host");
        Assert.Equal(362.00m, draft.Total);
        var issued = await service.IssueAsync(draft.Id, confirm: true);

        Assert.Equal(FiscalDocumentStatus.Issued, issued.Status);
        Assert.Equal("2026/1", issued.Number);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, (await db.BookingDocumentIssuers.SingleAsync(i => i.BookingId == portal)).Issuer);
        Assert.Single(await db.FiscalDocuments.Where(d => d.BookingId == legacy || d.BookingId == portal).ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule. An issued document keeps the issuer it was issued with: renaming the issuer (and moving it) after the issue changes neither
    /// the stored snapshot nor what the PDF of the document shows, while a new draft shows the issuer of today. The document also keeps its purpose, its
    /// property, its period, the version of its rule and its payment channel.
    /// </summary>
    [Fact]
    public async Task FiscalDocument_IssuedPdfUsesSnapshot_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var second = await SeedBookingAsync(db, BookingSource.Manual);
        var service = Service(db);
        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);

        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == issued.Id);
        var snapshot = FiscalIssuerSnapshot.FromJson(stored.IssuerSnapshot);
        Assert.NotNull(snapshot);
        Assert.Equal("TEST Casa Vacanze Srl", snapshot.LegalName);
        Assert.Equal("01234567897", snapshot.VatNumber);
        Assert.Equal(FiscalDocumentPurpose.GuestStay, stored.Purpose);
        Assert.Equal(FiscalPaymentChannel.Unknown, stored.PaymentChannel);
        Assert.False(string.IsNullOrWhiteSpace(stored.RuleVersion));
        Assert.Equal(await db.Bookings.Where(b => b.Id == booking).Select(b => b.PropertyId).SingleAsync(), stored.PropertyId);
        Assert.Equal(new DateOnly(2026, 11, 1), stored.ServicePeriodFrom);
        Assert.Equal(new DateOnly(2026, 11, 4), stored.ServicePeriodTo);

        // The issuer is renamed and moves.
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();
        profile.LegalName = "TEST Nuovo Nome Srl";
        profile.AddressStreet = "Via Nuova";
        await db.SaveChangesAsync();

        var issuedPdf = TextOf(await service.PdfContentAsync(issued.Id, "it"));
        Assert.Contains("TEST Casa Vacanze Srl", issuedPdf, StringComparison.Ordinal);
        Assert.Contains("Via di Test", issuedPdf, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST Nuovo Nome Srl", issuedPdf, StringComparison.Ordinal);
        Assert.DoesNotContain("Via Nuova", issuedPdf, StringComparison.Ordinal);
        Assert.Equal(stored.IssuerSnapshot, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == issued.Id)).IssuerSnapshot);

        // A draft has no snapshot yet: it shows the issuer of today.
        var draft = await service.CreateDraftAsync(second, null);
        Assert.Null(draft.IssuerSnapshot);
        Assert.Contains("TEST Nuovo Nome Srl", TextOf(await service.PdfContentAsync(draft.Id, "it")), StringComparison.Ordinal);
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

    /// <summary>
    /// PROOF TEST: data rule (E11/F01/US01/T01, E14). Only the credit note that completes the credit marks the original <c>CreditedInFull</c> (an owner invoice
    /// credited in full frees its owner and period): a partial one does not, one refused for exceeding the original marks nothing, the last part does, and
    /// so does a single note of the whole document.
    /// </summary>
    [Fact]
    public async Task FiscalDocument_CreditNote_MarksTheOriginalCreditedInFullOnlyWhenItCompletesTheCredit_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var wholeBooking = await SeedBookingAsync(db, BookingSource.Manual);
        var service = Service(db);
        var original = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);
        var whole = await service.IssueAsync((await service.CreateDraftAsync(wholeBooking, null)).Id, confirm: true);
        Assert.False(await IsCreditedInFullAsync(db, original.Id));

        await service.CreateCreditNoteAsync(original.Id, 100m, "TEST rimborso parziale", confirm: true, null);
        Assert.False(await IsCreditedInFullAsync(db, original.Id));

        await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateCreditNoteAsync(original.Id, 262.01m, "TEST", confirm: true, null));
        Assert.False(await IsCreditedInFullAsync(db, original.Id));

        await service.CreateCreditNoteAsync(original.Id, null, "TEST storno del residuo", confirm: true, null);
        Assert.True(await IsCreditedInFullAsync(db, original.Id));

        // One note of the whole document completes the credit at once; the other documents are untouched.
        Assert.False(await IsCreditedInFullAsync(db, whole.Id));
        await service.CreateCreditNoteAsync(whole.Id, null, "TEST storno totale", confirm: true, null);
        Assert.True(await IsCreditedInFullAsync(db, whole.Id));
        Assert.Equal(2, await db.FiscalDocuments.AsNoTracking().CountAsync(d => d.CreditedInFull));
    }

    private static async Task<bool> IsCreditedInFullAsync(AppDbContext db, Guid documentId) =>
        (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId)).CreditedInFull;

    /// <summary>A booking that is not active owes no document: no draft, no number, no lock of the issuer (the one definition of an active booking).</summary>
    [Theory]
    [InlineData(BookingStatus.Pending, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Confirmed, true)]
    public async Task FiscalDocument_BookingNotActive_GetsNoDraftNoNumberNoLock_Test(BookingStatus status, bool noShow)
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var stored = await db.Bookings.SingleAsync(b => b.Id == booking);
        stored.Status = status;
        stored.NoShowAt = noShow ? Now.UtcDateTime : null;
        await db.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).CreateDraftAsync(booking, null));

        Assert.Equal(FiscalDocumentErrors.BookingNotActive, refused.Code);
        Assert.Empty(await db.FiscalDocuments.ToListAsync());
        Assert.Empty(await db.FiscalNumberSequences.ToListAsync());
        Assert.Empty(await db.BookingDocumentIssuers.ToListAsync());
    }

    /// <summary>The payment state of a document CasaZen issued is the current one, read from the payments, not the one of the day the draft was made.</summary>
    [Fact]
    public async Task FiscalDocument_PaymentState_IsTheCurrentOneWhenRead_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Direct);
        var service = Service(db);
        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, (await service.GetAsync(issued.Id))!.PaymentState);

        // The guest pays the whole total: the document is Paid for the API (the stored value is not what is shown).
        var payment = new Payment { OrgId = OrgId, BookingId = booking, Amount = 362m, Status = PaymentStatus.Completed, ProcessedAt = Now.UtcDateTime };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        Assert.Equal(FiscalDocumentPaymentState.Paid, (await service.GetAsync(issued.Id))!.PaymentState);
        var page = await service.ListAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 1, 20);
        Assert.Equal(FiscalDocumentPaymentState.Paid, Assert.Single(page.Items).PaymentState);

        // A partial refund: what is collected net of it no longer reaches the total.
        payment.RefundedAmount = 100m;
        payment.Status = PaymentStatus.PartiallyRefunded;
        await db.SaveChangesAsync();
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, (await service.GetAsync(issued.Id))!.PaymentState);
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, Assert.Single((await service.ListAsync(new HostScope(OrgId), new FiscalDocumentCriteria(), 1, 20)).Items).PaymentState);
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

    /// <summary>
    /// PROOF TEST: data rule. The credit note of a receipt (an issuer without a VAT number, whose rule gives a receipt) is no electronic invoice: the XML builder
    /// is never called, the SDI state is <c>not_applicable</c>, and its PDF keeps the nature codes (N2.2) internal and carries no courtesy copy. The credit note
    /// of an invoice stays an electronic one (XML built, <c>to_send</c>).
    /// </summary>
    [Fact]
    public async Task FiscalDocument_CreditNoteOfAReceipt_IsNoElectronicInvoice_Test()
    {
        await using var db = NewDb();
        var booking = await SeedAsync(db, BookingSource.Manual, regime: DocumentRegime.PrivatePerson);
        var xml = new Mock<IFatturaPaXmlBuilder>(MockBehavior.Strict);
        var service = Service(db, xmlBuilder: xml.Object);
        var receipt = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);
        Assert.Equal(DocumentKind.Receipt, receipt.Kind);

        var part = await service.CreateCreditNoteAsync(receipt.Id, 100m, "TEST rimborso parziale", confirm: true, null);
        var rest = await service.CreateCreditNoteAsync(receipt.Id, null, "TEST storno del residuo", confirm: true, null);

        Assert.Equal(receipt.Total, part.Total + rest.Total);
        foreach (var note in new[] { part, rest })
        {
            Assert.Equal(DocumentKind.CreditNote, note.Kind);
            Assert.Null(note.FatturaPaXml);
            Assert.Equal(FiscalDocumentSdiStatuses.NotApplicable, note.SdiStatus);
            var text = TextOf(await service.PdfContentAsync(note.Id, "it"));
            Assert.DoesNotContain("N2.2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Copia di cortesia", text, StringComparison.OrdinalIgnoreCase);
        }

        xml.Verify(x => x.Build(It.IsAny<FatturaPaInput>()), Times.Never);

        // The credit note of an invoice stays an electronic one.
        await using var invoiceDb = NewDb();
        var invoiceBooking = await SeedAsync(invoiceDb, BookingSource.Direct);
        var invoiceService = Service(invoiceDb);
        var invoice = await invoiceService.IssueAsync((await invoiceService.CreateDraftAsync(invoiceBooking, null)).Id, confirm: true);
        var creditNote = await invoiceService.CreateCreditNoteAsync(invoice.Id, 100m, "TEST rimborso parziale", confirm: true, null);
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, creditNote.SdiStatus);
        Assert.Equal("<p:FatturaElettronica/>", creditNote.FatturaPaXml);
    }

    private static string Snapshot(FiscalDocument d) =>
        string.Join('|', d.Number, d.Status, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.RecipientName, d.RecipientFiscalCode, d.IssuerSnapshot, d.RuleVersion,
            string.Join(',', d.Lines.OrderBy(l => l.LineNumber).Select(l => $"{l.LineNumber}:{l.LineType}:{l.Description}:{l.TotalPrice}:{l.VatRatePercent}")));

    /// <summary>Every text of a PDF content: headings, paragraphs and the cells of the tables, one per line.</summary>
    private static string TextOf(Casazen.Core.Documents.PdfDocumentContent content) =>
        string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static IEnumerable<string> TextsOf(Casazen.Core.Documents.PdfBlock block) => block switch
    {
        Casazen.Core.Documents.PdfHeading heading => new[] { heading.Text },
        Casazen.Core.Documents.PdfParagraph paragraph => new[] { paragraph.Text },
        Casazen.Core.Documents.PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => Array.Empty<string>(),
    };

    private static FiscalDocumentService Service(AppDbContext db, IFeatureFlags? flags = null, IFatturaPaXmlBuilder? xmlBuilder = null)
    {
        var featureFlags = flags ?? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var issuers = new DocumentIssuerService(db, featureFlags, rules, environment.Object, new FakeTimeProvider(Now), NullLogger<DocumentIssuerService>.Instance);
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
            xmlBuilder ?? xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            rules,
            new FakeTimeProvider(Now),
            NullLogger<FiscalDocumentService>.Instance);
    }

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
            IssuerMode = DocumentIssuerMode.CasaZen,
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
