using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/DOC-RECIPIENT (privacy and data rules). A document that needs only a name (a Swiss document of any kind, an Italian receipt) is made out to the guest of the booking, by name,
/// when the booking has no invoice data (the portals and the site of the agency send none): the name and nothing else of the guest. The invoice data of the booking wins when it has a
/// name; an Italian invoice, a guest who asked for an invoice without giving its data and a guest who was erased never fall back.
/// </summary>
public class FiscalDocumentRecipientProofTests
{
    private const string GuestName = "Marta Bianchi";

    /// <summary>What the guest of the seeded bookings gave beyond the name: none of it may reach a document.</summary>
    private static readonly string[] GuestPrivateData = ["marta.bianchi.privata@example.org", "+41790001122", "Via Privata 7", "Zurigo", "Liechtenstein", "Liechtensteinese"];

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// PROOF TEST: privacy rule. A Swiss booking of a portal has no invoice data: it gets a draft and an issued CHF document made out to the guest of the booking, and the recipient
    /// snapshot holds the name and nothing else of the guest (no address, e-mail, phone, fiscal code or nationality: not in the stored columns, not on the PDF).
    /// </summary>
    [Fact]
    public async Task Document_SwissBookingWithoutInvoiceData_UsesGuestNameOnly_Test()
    {
        await using var db = NewDb();
        var booking = await SeedSwissAsync(db);
        var service = Service(db);

        var draft = await service.CreateDraftAsync(booking, null);
        Assert.Equal((FiscalDocumentStatus.Draft, "CHF", GuestName), (draft.Status, draft.Currency, draft.RecipientName));

        var issued = await service.IssueAsync(draft.Id, confirm: true);
        Assert.Equal((FiscalDocumentStatus.Issued, "CHF", "ch-vat-2024.1", "2026/1"), (issued.Status, issued.Currency, issued.RuleVersion, issued.Number));

        // The columns as the database holds them: the name, that the recipient is a private person, and nothing else.
        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == issued.Id);
        Assert.Equal((InvoiceRecipientKind.Guest, GuestName), (stored.RecipientKind, stored.RecipientName));
        Assert.All(RestOfTheRecipient(stored), value => Assert.Null(value));

        // The PDF shows the name, and nothing else the guest gave.
        var text = TextOf(await service.PdfContentAsync(issued.Id, "it"));
        Assert.Contains(GuestName, text, StringComparison.Ordinal);
        foreach (var secret in GuestPrivateData)
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: data rule. The Italian invoice did not loosen: a booking of an issuer whose rule is an invoice, with no invoice data (or a row with no name that holds no «yes» to
    /// the invoice), is refused with the same code and the same field as before, whatever name the guest has; nothing is made.
    /// </summary>
    [Fact]
    public async Task Document_ItalianInvoiceWithoutInvoiceData_StillRefused_Test()
    {
        await using var db = NewDb();
        var booking = await SeedItalianAsync(db, DocumentRegime.VatOrdinary);

        var none = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, none.Code);
        Assert.Equal(new[] { InvoiceDataFields.Name }, Assert.IsAssignableFrom<IReadOnlyList<string>>(none.Extensions["fields"]));

        // A row with no name and no request for an invoice is the same refusal; the fields it names are those of the checks of an invoice (the name first, then the address).
        var noName = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db, id => Row(id, name: null, requested: false)).CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, noName.Code);
        Assert.Contains(InvoiceDataFields.Name, Assert.IsAssignableFrom<IReadOnlyList<string>>(noName.Extensions["fields"]));

        // A name alone is still not an invoice: the checks of an invoice are the ones they were.
        var nameOnly = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db, id => Row(id, name: "TEST Cliente", requested: true)).CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, nameOnly.Code);
        Assert.Contains(InvoiceDataFields.AddressStreet, Assert.IsAssignableFrom<IReadOnlyList<string>>(nameOnly.Extensions["fields"]));

        Assert.Empty(await db.FiscalDocuments.ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: privacy rule. A guest who was erased (anonymized: its name is the placeholder of the erasure), who asked to be erased or who was deleted never gets a document made
    /// out to a name that is no longer theirs: the refusal is the one of a booking with no invoice data, and nothing is made.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, "ANONYMIZED", "ANONYMIZED")]
    [InlineData(false, true, false, "Marta", "Bianchi")]
    [InlineData(false, false, true, "Marta", "Bianchi")]
    [InlineData(false, false, false, "ANONYMIZED", "ANONYMIZED")]
    public async Task Document_ErasedGuest_NeverGetsADocument_Test(bool anonymized, bool erasureRequested, bool deleted, string firstName, string lastName)
    {
        await using var db = NewDb();
        var booking = await SeedSwissAsync(db, guest =>
        {
            guest.FirstName = firstName;
            guest.LastName = lastName;
            guest.DataAnonymizedDate = anonymized ? Now.UtcDateTime : null;
            guest.ErasureRequested = erasureRequested;
            guest.IsDeleted = deleted;
        });
        var service = Service(db);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateDraftAsync(booking, null));

        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, refused.Code);
        Assert.Equal(new[] { InvoiceDataFields.Name }, Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.Extensions["fields"]));
        Assert.Empty(await db.FiscalDocuments.ToListAsync());
    }

    /// <summary>
    /// An Italian private owner (a receipt) with no invoice data: the receipt is made out to the guest by name, as a Swiss document is. The receipt has no XML and nothing for the SDI.
    /// </summary>
    [Fact]
    public async Task Document_ItalianReceiptWithoutInvoiceData_UsesTheGuestName_Test()
    {
        await using var db = NewDb();
        var booking = await SeedItalianAsync(db, DocumentRegime.PrivatePerson);
        var service = Service(db);

        var draft = await service.CreateDraftAsync(booking, null);

        Assert.Equal((DocumentKind.Receipt, "it-private-2026.1", "EUR", GuestName), (draft.Kind, draft.RuleVersion, draft.Currency, draft.RecipientName));
        Assert.Equal(InvoiceRecipientKind.Guest, draft.RecipientKind);
        Assert.All(RestOfTheRecipient(draft), value => Assert.Null(value));
    }

    /// <summary>
    /// The invoice data of the booking wins over the name of the guest (the row with a name is the recipient, with its address, as it always was), and a guest who asked for an invoice
    /// without giving any data is waited for: the document is not made out to the guest's name behind its back.
    /// </summary>
    [Fact]
    public async Task Document_InvoiceDataOfTheBooking_WinsOverTheGuestName_AndARequestedInvoiceWaitsForIt_Test()
    {
        await using var db = NewDb();
        var booking = await SeedSwissAsync(db);

        var asked = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db, id => Row(id, name: null, requested: true)).CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, asked.Code);
        Assert.Empty(await db.FiscalDocuments.ToListAsync());

        var draft = await Service(db, id => Row(id, name: "TEST Studio Rossi", requested: false, street: "Bahnhofstrasse 1", city: "Zurigo")).CreateDraftAsync(booking, null);

        Assert.Equal(("TEST Studio Rossi", "Bahnhofstrasse 1", "Zurigo"), (draft.RecipientName, draft.RecipientAddressStreet, draft.RecipientCity));
        var text = TextOf(await Service(db).PdfContentAsync(draft.Id, "it"));
        Assert.Contains("TEST Studio Rossi", text, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestName, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: data rule. A draft made out to the guest by name (the booking had no invoice data) is no longer the document once the booking has invoice data with a name: the issue is
    /// refused (409 <c>document_draft_outdated</c>, nothing numbered, the draft stays a draft), and the draft made again takes the invoice data and is issued with it.
    /// </summary>
    [Fact]
    public async Task Document_DraftMadeWithTheGuestName_IsOutdatedOnceTheBookingHasInvoiceData_Test()
    {
        await using var db = NewDb();
        var booking = await SeedSwissAsync(db);
        string? typed = null;
        var service = Service(db, id => typed is null ? null : Row(id, name: typed, requested: false, street: "Bahnhofstrasse 1", city: "Zurigo"));

        var draft = await service.CreateDraftAsync(booking, null);
        Assert.Equal(GuestName, draft.RecipientName);

        typed = "TEST Studio Rossi";
        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => service.IssueAsync(draft.Id, confirm: true));

        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        var kept = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentStatus.Draft, (string?)null, GuestName), (kept.Status, kept.Number, kept.RecipientName));

        await service.RefreshDraftAsync(draft.Id);
        var issued = await service.IssueAsync(draft.Id, confirm: true);

        Assert.Equal((FiscalDocumentStatus.Issued, "TEST Studio Rossi", "Bahnhofstrasse 1"), (issued.Status, issued.RecipientName, issued.RecipientAddressStreet));
    }

    /// <summary>
    /// PROOF TEST: privacy rule. A guest who was erased (anonymized), who asked to be erased or who was deleted after the draft was made out to their name: the issue is refused (409
    /// <c>document_draft_outdated</c>, nothing numbered) and the draft made again is the refusal of a booking with no invoice data: no document goes out to a name that is no longer theirs.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Document_DraftMadeWithTheGuestName_IsOutdatedWhenTheGuestIsErasedAfterwards_Test(bool anonymized, bool erasureRequested, bool deleted)
    {
        await using var db = NewDb();
        var booking = await SeedSwissAsync(db);
        var service = Service(db);
        var draft = await service.CreateDraftAsync(booking, null);

        var guestId = await db.Bookings.Where(b => b.Id == booking).Select(b => b.GuestId).SingleAsync();
        var guest = await db.Guests.SingleAsync(g => g.Id == guestId);
        guest.DataAnonymizedDate = anonymized ? Now.UtcDateTime : null;
        guest.ErasureRequested = erasureRequested;
        guest.IsDeleted = deleted;
        if (anonymized)
        {
            guest.FirstName = GuestDataEraser.AnonymizedName;
            guest.LastName = GuestDataEraser.AnonymizedName;
        }

        await db.SaveChangesAsync();

        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => service.IssueAsync(draft.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        Assert.Equal(FiscalDocumentStatus.Draft, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).Status);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.RefreshDraftAsync(draft.Id));
        Assert.Equal(FiscalDocumentErrors.InvoiceDataMissing, refused.Code);
    }

    /// <summary>The recipient columns beyond the name and the kind of recipient: every one is empty on a document made out to the guest by name.</summary>
    private static string?[] RestOfTheRecipient(FiscalDocument document)
    {
        return new[]
        {
            document.RecipientFiscalCode,
            document.RecipientVatNumber,
            document.RecipientAddressStreet,
            document.RecipientAddressCivic,
            document.RecipientPostalCode,
            document.RecipientCity,
            document.RecipientProvince,
            document.RecipientCountryCode,
            document.RecipientSdiCode,
            document.RecipientPec,
        };
    }

    /// <summary>The invoice data of a booking as the host or the check-in gave it: a private person, in Switzerland.</summary>
    private static InvoiceDataView Row(Guid bookingId, string? name, bool requested, string? street = null, string? city = null) =>
        new(bookingId, requested, requested ? Now.UtcDateTime : null, InvoiceRecipientKind.Guest, name, null, null, street, null, null, city, null, "CH", null, null, InvoiceDataSource.Host, Now.UtcDateTime);

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

    /// <summary>The service over <paramref name="db"/>; <paramref name="invoiceData"/> gives the invoice data of a booking (none by default: the booking of a portal).</summary>
    private static FiscalDocumentService Service(AppDbContext db, Func<Guid, InvoiceDataView?>? invoiceData = null)
    {
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance), rules, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var data = new Mock<IInvoiceDataService>();
        data.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => invoiceData?.Invoke(bookingId));
        return new FiscalDocumentService(
            db,
            issuers,
            data.Object,
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            Mock.Of<IFatturaPaXmlBuilder>(),
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            rules,
            time,
            NullLogger<FiscalDocumentService>.Instance);
    }

    /// <summary>An agency in Lugano that issues Swiss documents with its VAT number, and a booking of a portal (CHF, no invoice data of its own).</summary>
    private static async Task<Guid> SeedSwissAsync(AppDbContext db, Action<Guest>? shapeGuest = null)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(NewOrg(orgId, hasVat: false));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = DocumentRegime.SwissVatRegistered,
            VatRegistered = true,
            SwissVatNumber = "CHE-109.322.551",
            LegalName = "TEST Quokka Sagl",
            AddressStreet = "Via Nassa",
            AddressCivic = "5",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
        });
        return await SeedBookingAsync(db, orgId, "CHF", shapeGuest);
    }

    /// <summary>An issuer in Italy under <paramref name="regime"/> (an invoice, or the receipt of a private person), and a booking of a portal (EUR, no invoice data of its own).</summary>
    private static async Task<Guid> SeedItalianAsync(AppDbContext db, DocumentRegime regime)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(NewOrg(orgId, hasVat: true));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = regime,
            VatRegistered = regime != DocumentRegime.PrivatePerson,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
        });
        return await SeedBookingAsync(db, orgId, "EUR", shapeGuest: null);
    }

    private static async Task<Guid> SeedBookingAsync(AppDbContext db, Guid orgId, string currency, Action<Guest>? shapeGuest)
    {
        var property = new Property { OrgId = orgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = currency };
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Marta",
            LastName = "Bianchi",
            Email = "marta.bianchi.privata@example.org",
            PhoneNumber = "+41790001122",
            Address = "Via Privata 7",
            City = "Zurigo",
            PostalCode = "8001",
            Country = "Liechtenstein",
            Nationality = "Liechtensteinese",
        };
        shapeGuest?.Invoke(guest);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 14),
            CheckOutDate = new DateTime(2026, 11, 18),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.BookingCom,
            BasePrice = 500m,
            CleaningFee = 0m,
            TotalPrice = 500m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static OrgEntity NewOrg(Guid id, bool hasVat) => new()
    {
        Id = id,
        Name = "TEST Org",
        Slug = $"test-{id:N}"[..20],
        DisplayName = "TEST Org",
        ContactEmail = "test@example.com",
        HasPartitaIva = hasVat,
        PartitaIvaNumber = hasVat ? "01234567897" : null,
        FiscalCode = hasVat ? "01234567897" : null,
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalDocumentRecipient_{Guid.NewGuid()}").Options, new RecipientTenant(), new EphemeralDataProtectionProvider());
}

file sealed class RecipientTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
