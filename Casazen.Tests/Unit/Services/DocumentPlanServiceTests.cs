using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
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
/// E17/F01/US01/T02 (NF-116, NF-83, D12): the documents of a booking and who issues each. An Italian property's stay is documented by its owner (a receipt of a
/// private owner with the co-owners and the stamp duty, the FatturaPA of a company in its own series), the agency's services to the guest are a document of the
/// agency, an owner without an issuer profile gets a «Da fare» and no document, and a booking never has two documents of the same issuer and purpose.
/// </summary>
public class DocumentPlanServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// PROOF TEST: money rule (⚖ I4, I6). Two private co-owners at 50%: one receipt, issued on the profile of the co-owner who holds the mandate, numbered in that
    /// owner's own series, with the stamp duty of 2.00 above 77.47 of amounts without VAT (borne by the issuer: the total is what the guest paid), listing both
    /// co-owners with their shares, the stamp reference of the profile and «Emessa tramite» the agency; no XML (a receipt is no electronic invoice).
    /// </summary>
    [Fact]
    public async Task DocumentPlan_PrivateOwnerReceiptWithStampDuty_Test()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var signatory = await world.OwnerAsync("TEST Mario Rossi", 50m, mandate: true, OwnerProfile.Private);
        var other = await world.OwnerAsync("TEST Anna Bianchi", 50m, mandate: false, OwnerProfile.None);
        var booking = await world.BookingAsync();

        var plan = await world.Plans.PlanAsync(booking);
        var planned = Assert.Single(plan.Documents);
        Assert.Equal((world.ProfileOf(signatory), signatory, false), (planned.IssuerProfileId, planned.IssuerOwnerId, planned.ForAgencyServices));
        Assert.Equal(
            new[] { ("TEST Anna Bianchi", 50m), ("TEST Mario Rossi", 50m) },
            planned.CoOwners.Select(c => (c.Name, c.SharePercent)).Order());
        Assert.Contains(other, planned.CoOwners.Select(c => c.OwnerId));

        var draft = await world.Service.CreateDraftAsync(booking, null);
        Assert.Equal((DocumentKind.Receipt, world.ProfileOf(signatory)), (draft.Kind, draft.IssuerProfileId));
        Assert.Equal((2.00m, 362.00m, 0m), (draft.StampDuty, draft.Total, draft.VatAmount));

        var receipt = await world.Service.IssueAsync(draft.Id, confirm: true);
        Assert.Equal("P2026/1", receipt.Number);
        Assert.Null(receipt.FatturaPaXml);
        var snapshot = FiscalIssuerSnapshot.FromJson(receipt.IssuerSnapshot)!;
        Assert.Equal(("TEST Mario Rossi", "RSSMRA80A01H501U", "TEST-BOLLO-1"), (snapshot.LegalName, snapshot.FiscalCode, snapshot.StampDutyReference));
        Assert.Null(snapshot.VatNumber);
        Assert.Equal("TEST Agenzia Lago Srl", snapshot.Intermediary!.Name);
        Assert.Equal(2, snapshot.CoOwners!.Count);

        var text = TextOf(await world.Service.PdfContentAsync(receipt.Id, "it"));
        Assert.Contains("Emessa tramite TEST Agenzia Lago Srl per conto del proprietario", text, StringComparison.Ordinal);
        Assert.Contains("TEST Anna Bianchi (50%)", text, StringComparison.Ordinal);
        Assert.Contains("Imposta di bollo assolta: TEST-BOLLO-1", text, StringComparison.Ordinal);
        world.Xml.Verify(x => x.Build(It.IsAny<FatturaPaInput>()), Times.Never);
    }

    /// <summary>
    /// ⚖ I4: a document with a stamp duty says how it is paid: the reference of its issuer, the virtual stamp the issuer confirmed (kept in the snapshot), else
    /// «da assolvere»; a snapshot written before the issuer said it (a document issued before E17) prints nothing new.
    /// </summary>
    [Fact]
    public async Task StampDuty_SaysHowItIsPaid_AndADocumentIssuedBeforeStaysAsItWas()
    {
        var texts = new ResxDocumentTexts();
        Assert.Equal(
            "Imposta di bollo assolta: TEST-BOLLO-1",
            FiscalDocumentPdfs.StampDutyLine(new FiscalIssuerSnapshot { StampDutyReference = "TEST-BOLLO-1", VirtualStampConfirmed = false }, texts, "it"));
        Assert.Equal("Imposta di bollo da assolvere", FiscalDocumentPdfs.StampDutyLine(new FiscalIssuerSnapshot { VirtualStampConfirmed = false }, texts, "it"));
        Assert.Null(FiscalDocumentPdfs.StampDutyLine(FiscalIssuerSnapshot.FromJson("""{"version":1,"legalName":"TEST Agenzia Lago Srl"}""")!, texts, "it"));

        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var organization = await world.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.Id == world.OrganizationProfile);
        organization.VirtualStampConfirmed = true;
        var snapshot = FiscalIssuerSnapshot.FromJson(FiscalIssuerSnapshot.From(organization, await world.Db.Orgs.SingleAsync()).ToJson())!;
        Assert.Equal("Imposta di bollo assolta in modo virtuale", FiscalDocumentPdfs.StampDutyLine(snapshot, texts, "it"));
    }

    /// <summary>
    /// PROOF TEST: data rule. A company owner: a FatturaPA invoice with the company as cedente (its VAT number, never the agency's), numbered in the company's own
    /// sezionale; the organization's own documents keep their series and counter (both have their number 1).
    /// </summary>
    [Fact]
    public async Task DocumentPlan_CompanyOwnerInvoiceUsesOwnerSeries_Test()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var company = await world.OwnerAsync("TEST Immobiliare Lago Srl", 100m, mandate: true, OwnerProfile.Company);
        var booking = await world.BookingAsync();

        var invoice = await world.Service.IssueAsync((await world.Service.CreateDraftAsync(booking, null)).Id, confirm: true);

        Assert.Equal((DocumentKind.Invoice, world.ProfileOf(company)), (invoice.Kind, invoice.IssuerProfileId));
        Assert.Equal("SRL2026/1", invoice.Number);
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, invoice.SdiStatus);
        var input = Assert.Single(world.XmlInputs);
        Assert.Equal(new FatturaPaId("IT", "01234567897"), input.Issuer.VatId);
        Assert.Equal("TEST Immobiliare Lago Srl", input.Issuer.Denomination);

        // E17/F01/US01/T03: the agency is the third-party issuer (TZ) and the transmitter of the owner's invoice.
        Assert.Equal(FatturaPaIssuedBy.ThirdParty, input.IssuedBy);
        Assert.Equal(("TEST Agenzia Lago Srl", new FatturaPaId("IT", "09876543217")), (input.ThirdPartyIssuer!.Denomination, input.ThirdPartyIssuer.VatId));
        Assert.Equal(new FatturaPaId("IT", "09876543217"), input.Transmitter);
        Assert.Equal(1, (await world.Db.FiscalNumberSequences.SingleAsync(s => s.IssuerProfileId == world.ProfileOf(company))).LastNumber);

        // A booking of a property without owners is the agency's, in the agency's own series: its number 1 too.
        var own = await world.BookingAsync(withOwners: false);
        var agencyDocument = await world.Service.IssueAsync((await world.Service.CreateDraftAsync(own, null)).Id, confirm: true);
        Assert.Equal((world.OrganizationProfile, "2026/1"), (agencyDocument.IssuerProfileId!.Value, agencyDocument.Number));
    }

    /// <summary>
    /// PROOF TEST: data rule. The agency treats the cleaning as its service: the booking of a company owner gets the owner's document of the stay and the agency's
    /// document of the cleaning, both issued (two issuers, one row each in the issuer rows of the booking); a third draft of the plan is refused (409
    /// <c>document_already_issued</c>), and so is a second document of the owner forced as a draft: never two documents of the same issuer and purpose.
    /// </summary>
    [Fact]
    public async Task DocumentPlan_NeverTwoDocumentsSameIssuerAndPurpose_Test()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.Service);
        var company = await world.OwnerAsync("TEST Immobiliare Lago Srl", 100m, mandate: true, OwnerProfile.Company);
        var booking = await world.BookingAsync();

        var plan = await world.Plans.PlanAsync(booking);
        Assert.Equal([false, true], plan.Documents.Select(d => d.ForAgencyServices));

        await world.Service.CreateDraftAsync(booking, null);
        var drafts = await world.Db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.BookingId == booking).ToListAsync();
        var stay = Assert.Single(drafts, d => !d.ForAgencyServices);
        var services = Assert.Single(drafts, d => d.ForAgencyServices);
        Assert.Equal(world.ProfileOf(company), stay.IssuerProfileId);
        Assert.Equal(world.OrganizationProfile, services.IssuerProfileId);
        Assert.DoesNotContain(stay.Lines, l => l.LineType == FiscalLineType.Cleaning);
        Assert.Equal([FiscalLineType.Cleaning], services.Lines.Select(l => l.LineType));
        Assert.Equal(362.00m, stay.Total + services.Total);

        await world.Service.IssueAsync(stay.Id, confirm: true);
        await world.Service.IssueAsync(services.Id, confirm: true);
        Assert.Equal(2, await world.Db.BookingDocumentIssuers.CountAsync(i => i.BookingId == booking));

        var again = await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.AlreadyIssued, again.Code);

        var forced = new FiscalDocument
        {
            OrgId = OrgId,
            BookingId = booking,
            IssuerProfileId = world.ProfileOf(company),
            Source = FiscalDocumentSource.CasaZen,
            Status = FiscalDocumentStatus.Draft,
            Kind = DocumentKind.Invoice,
            Currency = "EUR",
        };
        world.Db.FiscalDocuments.Add(forced);
        await world.Db.SaveChangesAsync();
        var second = await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.IssueAsync(forced.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.AlreadyIssued, second.Code);
        Assert.Null((await world.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == forced.Id)).Number);
    }

    /// <summary>
    /// PROOF TEST: money rule («doppia fattura»). The agency billed the whole stay while the property had no owner in force; the owner recorded afterwards, with a
    /// profile that can issue, never gets a second document of the same stay (409 <c>document_already_issued</c>, no draft): the guest is never billed twice. Only
    /// once the agency's document is credited in full is the owner's document of the stay made, in the owner's own series.
    /// </summary>
    [Fact]
    public async Task DocumentPlan_StayBilledByTheAgency_NoSecondDocumentOfTheOwner_DoubleInvoice_Test()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var booking = await world.BookingAsync();
        var agency = await world.Service.IssueAsync((await world.Service.CreateDraftAsync(booking, null)).Id, confirm: true);
        Assert.Equal((world.OrganizationProfile, 362.00m), (agency.IssuerProfileId!.Value, agency.Total));

        var company = await world.OwnerAsync("TEST Immobiliare Lago Srl", 100m, mandate: true, OwnerProfile.Company);
        Assert.Equal(world.ProfileOf(company), Assert.Single((await world.Plans.PlanAsync(booking)).Documents).IssuerProfileId);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.AlreadyIssued, refused.Code);
        Assert.False(await world.Db.FiscalDocuments.AnyAsync(d => d.BookingId == booking && d.Status == FiscalDocumentStatus.Draft));

        await world.Service.CreateCreditNoteAsync(agency.Id, null, "TEST storno", confirm: true, null);
        var owner = await world.Service.IssueAsync((await world.Service.CreateDraftAsync(booking, null)).Id, confirm: true);
        Assert.Equal((world.ProfileOf(company), "SRL2026/1"), (owner.IssuerProfileId!.Value, owner.Number));
    }

    /// <summary>
    /// A draft made before the owner of today was recorded (or one of before the owners issued, moved to the organization by the migration) is never issued in the
    /// name of the agency: an owner whose profile cannot issue gets its «Da fare» (422 <c>owner_issuer_missing</c>); with a profile that can issue the old draft is
    /// out of date (409 <c>document_draft_outdated</c>, no number) and, made again from the booking, it is the owner's, in the owner's series.
    /// </summary>
    [Fact]
    public async Task DocumentPlan_DraftOfAnIssuerThePlanNoLongerHas_IsNeverIssued()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var booking = await world.BookingAsync();
        var draft = await world.Service.CreateDraftAsync(booking, null);
        Assert.Equal(world.OrganizationProfile, draft.IssuerProfileId);
        var owner = await world.OwnerAsync("TEST Senza Profilo", 100m, mandate: true, OwnerProfile.None);

        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.IssueAsync(draft.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.OwnerIssuerMissing, missing.Code);
        world.Activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.EntityId == owner && r.Reason == FiscalDocumentTodos.OwnerIssuerMissingReason), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        var other = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var stay = await other.BookingAsync();
        var old = await other.Service.CreateDraftAsync(stay, null);
        var company = await other.OwnerAsync("TEST Immobiliare Lago Srl", 100m, mandate: true, OwnerProfile.Company);

        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => other.Service.IssueAsync(old.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        Assert.Null((await other.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == old.Id)).Number);

        Assert.Equal(other.ProfileOf(company), (await other.Service.RefreshDraftAsync(old.Id)).IssuerProfileId);
        Assert.Equal("SRL2026/1", (await other.Service.IssueAsync(old.Id, confirm: true)).Number);
    }

    /// <summary>
    /// An owner without an issuer profile that can issue: no draft at all (never a document in the agency's name in its place), 422 <c>owner_issuer_missing</c> for
    /// the draft and for the check-out path, and the «Da fare» of the owner.
    /// </summary>
    [Fact]
    public async Task DocumentPlan_OwnerWithoutIssuerProfile_NoDocumentAndATodo()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var owner = await world.OwnerAsync("TEST Senza Profilo", 100m, mandate: true, OwnerProfile.None);
        var booking = await world.BookingAsync();

        var plan = await world.Plans.PlanAsync(booking);
        Assert.Empty(plan.Documents);
        Assert.Equal([owner], plan.OwnersWithoutIssuer);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.CreateDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.OwnerIssuerMissing, refused.Code);
        var atCheckOut = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.EnsureDraftAsync(booking, null));
        Assert.Equal(FiscalDocumentErrors.OwnerIssuerMissing, atCheckOut.Code);
        Assert.False(await world.Db.FiscalDocuments.AnyAsync(d => d.BookingId == booking));
        world.Activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.EntityType == FiscalDocumentTodos.OwnerEntityType && r.EntityId == owner && r.Reason == FiscalDocumentTodos.OwnerIssuerMissingReason),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// A Swiss property keeps the agency as its issuer (E11/E12), unless the agency chose «emette il proprietario» for an owner with a Swiss profile (⚖ C1); a
    /// property without owners is the agency's.
    /// </summary>
    [Fact]
    public async Task DocumentPlan_SwissPropertyIsTheAgencys_UnlessTheOwnerIssues()
    {
        var world = await World.CreateAsync(CleaningFeeTreatment.FollowsStay);
        var owner = await world.OwnerAsync("TEST Hans Muster", 100m, mandate: true, OwnerProfile.Swiss);
        var booking = await world.BookingAsync(country: "CH");
        Assert.Equal(world.OrganizationProfile, Assert.Single((await world.Plans.PlanAsync(booking)).Documents).IssuerProfileId);

        var profile = await world.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.PropertyOwnerId == owner);
        profile.StayIssuedByOwner = true;
        await world.Db.SaveChangesAsync();
        Assert.Equal(profile.Id, Assert.Single((await world.Plans.PlanAsync(booking)).Documents).IssuerProfileId);

        // The same owner's stay in Italy is issued with the Italian rules: its Swiss profile does not issue it, the owner needs an Italian one («Da fare»).
        var italian = await world.Plans.PlanAsync(await world.BookingAsync());
        Assert.Empty(italian.Documents);
        Assert.Equal([owner], italian.OwnersWithoutIssuer);

        var withoutOwners = await world.BookingAsync(withOwners: false);
        Assert.Equal(world.OrganizationProfile, Assert.Single((await world.Plans.PlanAsync(withoutOwners)).Documents).IssuerProfileId);
    }

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

    private enum OwnerProfile
    {
        None,
        Private,
        Company,
        Swiss,
    }

    /// <summary>An agency with its complete profile and two properties (Como, Lugano), over one in-memory database with encryption, the shipped tax rules and the issuance on.</summary>
    private sealed class World
    {
        private readonly Dictionary<Guid, Guid> _profiles = [];
        private readonly Guid _propertyIt = Guid.NewGuid();
        private readonly Guid _propertyCh = Guid.NewGuid();

        private World(AppDbContext db, Guid organizationProfile)
        {
            Db = db;
            OrganizationProfile = organizationProfile;
            var rules = TaxRuleCatalogs.Resolver();
            var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
            var environment = new Mock<IHostEnvironment>();
            environment.SetupGet(e => e.EnvironmentName).Returns("Test");
            var issuers = new DocumentIssuerService(db, flags, rules, environment.Object, new FakeTimeProvider(Now), NullLogger<DocumentIssuerService>.Instance);
            var invoiceData = new Mock<IInvoiceDataService>();
            invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid bookingId, CancellationToken _) => new InvoiceDataView(
                    bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
                    "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime));
            Xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Callback<FatturaPaInput>(XmlInputs.Add).Returns("<p:FatturaElettronica/>");
            Activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            Plans = new DocumentPlanService(db, rules);
            Service = new FiscalDocumentService(
                db,
                issuers,
                invoiceData.Object,
                new FiscalNumberingService(db, new FakeTimeProvider(Now), NullLogger<FiscalNumberingService>.Instance),
                Xml.Object,
                Mock.Of<IDomainEventRecorder>(),
                new MigraDocPdfDocumentRenderer(),
                new ResxDocumentTexts(),
                rules,
                new FakeTimeProvider(Now),
                NullLogger<FiscalDocumentService>.Instance,
                activities: Activities.Object,
                featureFlags: flags,
                documentPlans: Plans);
        }

        public AppDbContext Db { get; }

        public Guid OrganizationProfile { get; }

        public DocumentPlanService Plans { get; }

        public FiscalDocumentService Service { get; }

        public Mock<IFatturaPaXmlBuilder> Xml { get; } = new();

        public List<FatturaPaInput> XmlInputs { get; } = [];

        public Mock<IInternalActivityService> Activities { get; } = new();

        public static async Task<World> CreateAsync(CleaningFeeTreatment agencyCleaning)
        {
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"DocumentPlan_{Guid.NewGuid()}").Options, new PlanTenant(), new EphemeralDataProtectionProvider());
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Agenzia Lago",
                Slug = $"plan-{Guid.NewGuid():N}"[..20],
                DisplayName = "TEST Agenzia Lago",
                ContactEmail = "agenzia@example.com",
                PlanTier = PlanTier.Agency,
                HasPartitaIva = true,
                PartitaIvaNumber = "09876543217",
                FiscalCode = "09876543217",
            });
            var organization = new OrgFiscalIssuerProfile
            {
                OrgId = OrgId,
                DocumentRegime = DocumentRegime.VatOrdinary,
                VatRegistered = true,
                LegalName = "TEST Agenzia Lago Srl",
                AddressStreet = "Via del Porto",
                AddressCivic = "2",
                PostalCode = "22100",
                City = "Como",
                Province = "CO",
                CountryCode = "IT",
                CleaningFeeTreatment = agencyCleaning,
            };
            db.OrgFiscalIssuerProfiles.Add(organization);
            await db.SaveChangesAsync();

            var world = new World(db, organization.Id);
            db.Properties.Add(Property(world._propertyIt, "TEST Casa Como", "IT", "EUR"));
            db.Properties.Add(Property(world._propertyCh, "TEST Casa Lugano", "CH", "CHF"));
            await db.SaveChangesAsync();
            return world;
        }

        public Guid ProfileOf(Guid owner) => _profiles[owner];

        /// <summary>An owner with a share of both properties from 2025 and, when asked, a mandate and an issuer profile.</summary>
        public async Task<Guid> OwnerAsync(string name, decimal share, bool mandate, OwnerProfile profile)
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", LegalName = name, Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            foreach (var property in new[] { _propertyIt, _propertyCh })
            {
                Db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = OrgId,
                    PropertyId = property,
                    PropertyOwnerId = owner.Id,
                    SharePercent = share,
                    OwnershipStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
            }

            if (mandate)
                Db.OwnerMandates.Add(new OwnerMandate { OrgId = OrgId, PropertyOwnerId = owner.Id, ValidFrom = new DateOnly(2025, 1, 1), CommissionPercent = 20m, CreatedAt = Now.UtcDateTime });

            var issuer = profile switch
            {
                OwnerProfile.Private => PrivateProfile(owner.Id, name),
                OwnerProfile.Company => CompanyProfile(owner.Id, name),
                OwnerProfile.Swiss => SwissProfile(owner.Id, name),
                _ => null,
            };
            if (issuer is not null)
            {
                Db.OrgFiscalIssuerProfiles.Add(issuer);
                _profiles[owner.Id] = issuer.Id;
            }

            await Db.SaveChangesAsync();
            return owner.Id;
        }

        /// <summary>A stay that ended in November 2026: 350 with 50 of cleaning and 12 of tourist tax collected by the host.</summary>
        public async Task<Guid> BookingAsync(string country = "IT", bool withOwners = true)
        {
            var propertyId = withOwners ? (country == "CH" ? _propertyCh : _propertyIt) : Guid.NewGuid();
            if (!withOwners)
                Db.Properties.Add(Property(propertyId, "TEST Casa Senza Proprietari", country, "EUR"));

            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 1),
                CheckOutDate = new DateTime(2026, 11, 4),
                NumberOfGuests = 2,
                Status = BookingStatus.CheckedOut,
                Source = BookingSource.Direct,
                BasePrice = 350m,
                CleaningFee = 50m,
                TouristTax = 12m,
                TotalPrice = 362m,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            await Db.SaveChangesAsync();
            return booking.Id;
        }

        private static Property Property(Guid id, string name, string country, string currency) =>
            new() { Id = id, OrgId = OrgId, OwnerId = "auth0|agenzia", Name = name, Address = "Via 1", City = "Como", Country = country, Currency = currency };

        private static OrgFiscalIssuerProfile PrivateProfile(Guid owner, string name) => new()
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            DocumentRegime = DocumentRegime.PrivatePerson,
            LegalName = name,
            FiscalCode = "RSSMRA80A01H501U",
            AddressStreet = "Via del Sole",
            AddressCivic = "12",
            PostalCode = "20121",
            City = "Milano",
            Province = "MI",
            CountryCode = "IT",
            GuestDocumentSeries = "P{year}",
            StampDutyReference = "TEST-BOLLO-1",
        };

        private static OrgFiscalIssuerProfile CompanyProfile(Guid owner, string name) => new()
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            DocumentRegime = DocumentRegime.VatOrdinary,
            VatRegistered = true,
            LegalName = name,
            VatNumber = "01234567897",
            FiscalCode = "01234567897",
            AddressStreet = "Via del Lago",
            AddressCivic = "3",
            PostalCode = "22100",
            City = "Como",
            Province = "CO",
            CountryCode = "IT",
            GuestDocumentSeries = "SRL{year}",
        };

        private static OrgFiscalIssuerProfile SwissProfile(Guid owner, string name) => new()
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            DocumentRegime = DocumentRegime.SwissNotRegistered,
            LegalName = name,
            AddressStreet = "Via Nassa",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
        };
    }
}

file sealed class PlanTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
