using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
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

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E14/F01/US01/T01 (NF-114, D9, ⚖ R4, I10, I11): the invoice of the property manager to an owner is made from a statement version, its lines are the ledger
/// lines of the version to the cent, its VAT on top is posted once to the owner ledger at the issue, the Swiss rate is the one of the catalog, a version of
/// another organization is not found, and the commissions of the portals are recharged only for the listings whose account the agency holds.
/// </summary>
public class OwnerInvoiceServiceTests
{
    /// <summary>
    /// PROOF TEST: money rule. The draft of a version has one commission line per property, one recharge for each flat fee and each charge, one expense
    /// advanced for each expense, and they equal the ledger lines of the version to the cent (a mandate «+ IVA»: the nets are the ledger amounts); the VAT is the
    /// catalog's per line type. A second call returns the same draft.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_LinesMatchVersionLedger_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync(rig.StandardMonth());

        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, "auth0|agency");

        Assert.Equal((FiscalDocumentPurpose.OwnerInvoice, FiscalDocumentStatus.Draft, (Guid?)rig.OwnerId, (Guid?)version.Id), (draft.Purpose, draft.Status, draft.RecipientOwnerId, draft.OwnerStatementVersionId));
        Assert.Equal(((int?)2026, (int?)10, "CHF"), (draft.PeriodYear, draft.PeriodMonth, draft.Currency));
        var entries = await rig.Db.OwnerLedgerEntries.AsNoTracking().ToListAsync();
        Assert.Equal(-entries.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(e => e.Amount), Sum(draft, FiscalLineType.Commission));
        Assert.Equal(
            -entries.Where(e => e.EntryType == OwnerLedgerEntryType.OwnerCharge).Sum(e => e.Amount),
            Sum(draft, FiscalLineType.Recharge));
        Assert.Equal(-entries.Where(e => e.EntryType == OwnerLedgerEntryType.OwnerExpense).Sum(e => e.Amount), Sum(draft, FiscalLineType.AdvancedExpense));

        // One line per property for the commission, one per flat fee and charge, one per expense: 1'000.00 and 200.00, 80.00 and 30.00, 40.00.
        Assert.Equal(new[] { 1000.00m, 200.00m }, draft.Lines.Where(l => l.LineType == FiscalLineType.Commission).OrderByDescending(l => l.TotalPrice).Select(l => l.TotalPrice));
        Assert.Equal(new[] { 80.00m, 30.00m }, draft.Lines.Where(l => l.LineType == FiscalLineType.Recharge).OrderByDescending(l => l.TotalPrice).Select(l => l.TotalPrice));
        Assert.Equal(40.00m, Assert.Single(draft.Lines, l => l.LineType == FiscalLineType.AdvancedExpense).TotalPrice);
        Assert.All(draft.Lines.Where(l => l.LineType != FiscalLineType.AdvancedExpense), l => Assert.Equal(8.1m, l.VatRatePercent));
        Assert.Equal((0m, 0m), (draft.Lines.Single(l => l.LineType == FiscalLineType.AdvancedExpense).VatRatePercent, draft.Lines.Single(l => l.LineType == FiscalLineType.AdvancedExpense).VatAmount ?? 0m));
        Assert.Equal((1350.00m, 106.11m, 1456.11m), (draft.TaxableBase, draft.VatAmount, draft.Total));

        // The portal commissions of listings whose account the owner holds (the default) are not recharged.
        Assert.DoesNotContain(draft.Lines, l => l.Description.Contains("Booking.com", StringComparison.Ordinal));

        var again = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, "auth0|agency");
        Assert.Equal(draft.Id, again.Id);
        Assert.Single(await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.Purpose == FiscalDocumentPurpose.OwnerInvoice).ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: money rule. Issuing numbers the invoice in the owner-invoice series and posts its VAT on top to the owner ledger once (an AgencyVat line of the
    /// VAT of the invoice): a second issue is refused and posts nothing, and the net of the owner is the ledger less the VAT. The draft version of the month is
    /// generated again with it.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_VatOnTopPostedOnceToLedger_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync(rig.StandardMonth());
        var before = await rig.Db.OwnerLedgerEntries.AsNoTracking().SumAsync(e => e.Amount);
        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);

        var issued = await rig.Invoices.IssueAsync(rig.OrgId, draft.Id, confirm: true, "auth0|agency");

        Assert.Equal((FiscalDocumentStatus.Issued, "PM2026/1", "PM2026"), (issued.Status, issued.Number, issued.Series));
        var vat = Assert.Single(await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat).ToListAsync());
        Assert.Equal((-106.11m, OwnerLedgerSourceType.OwnerInvoice, (Guid?)issued.Id, (Guid?)issued.Id), (vat.Amount, vat.SourceType, vat.SourceId, vat.SourceDocumentId));
        Assert.Equal((2026, 10, rig.OwnerId), (vat.PeriodYear, vat.PeriodMonth, vat.PropertyOwnerId));

        var twice = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Invoices.IssueAsync(rig.OrgId, draft.Id, confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.NotDraft, twice.Code);
        Assert.Single(await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat).ToListAsync());
        Assert.Single(await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync());

        // The owner is paid the ledger less the VAT, and the draft version of the month was generated again with the VAT in it.
        Assert.Equal(before - 106.11m, await rig.Db.OwnerLedgerEntries.AsNoTracking().SumAsync(e => e.Amount));
        var versions = await rig.Db.OwnerStatementVersions.AsNoTracking().OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(OwnerStatementVersionStatus.Superseded, versions[0].Status);
        Assert.Equal(before - 106.11m, versions[^1].ClosingBalance);
    }

    /// <summary>PROOF TEST: money rule. A Swiss commission of CHF 1,000 plus the VAT of 8.1% of the catalog is an invoice of CHF 1,081.00.</summary>
    [Fact]
    public async Task OwnerInvoice_SwissRate_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);

        var issued = await rig.Invoices.IssueAsync(rig.OrgId, (await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null)).Id, confirm: true, null);

        var line = Assert.Single(issued.Lines);
        Assert.Equal((FiscalLineType.Commission, 1000.00m, 8.1m, (decimal?)81.00m), (line.LineType, line.TotalPrice, line.VatRatePercent, line.VatAmount));
        Assert.Equal((1000.00m, 81.00m, 1081.00m, (decimal?)8.1m), (issued.TaxableBase, issued.VatAmount, issued.Total, issued.VatRatePercent));
        Assert.Equal("ch-vat-2024.1", issued.RuleVersion);
        Assert.Null(issued.FatturaPaXml);
        Assert.Equal(-81.00m, (await rig.Db.OwnerLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == OwnerLedgerEntryType.AgencyVat)).Amount);
    }

    /// <summary>
    /// PROOF TEST: permission (tenant) rule. A statement version or an owner invoice of another organization is not found (404): no draft, no issue, nothing
    /// written.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_OtherOrganizationVersion404_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync(rig.StandardMonth());
        var otherOrg = Guid.NewGuid();

        var draftOfOther = await Assert.ThrowsAsync<NotFoundException>(() => rig.Invoices.CreateDraftAsync(otherOrg, version.Id, null));
        Assert.Equal(OwnerAccountingErrors.VersionNotFound, draftOfOther.Code);
        Assert.Empty(await rig.Db.FiscalDocuments.AsNoTracking().ToListAsync());

        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        var issueOfOther = await Assert.ThrowsAsync<NotFoundException>(() => rig.Invoices.IssueAsync(otherOrg, draft.Id, confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.NotFound, issueOfOther.Code);
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await rig.Invoices.ListAsync(otherOrg, new OwnerInvoiceCriteria()));
        Assert.DoesNotContain(await rig.Db.OwnerLedgerEntries.AsNoTracking().ToListAsync(), e => e.EntryType == OwnerLedgerEntryType.AgencyVat);
    }

    /// <summary>
    /// PROOF TEST: money rule (⚖ I10, I11). With the listings on the default holder (the owner, V1) no portal commission is recharged; once the agency holds the
    /// account of one listing on Booking.com (V2, written in bulk), its commissions, and only its, are one recharge line «Riaddebito commissioni Booking.com»;
    /// with the recharge switched off there is none again.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_PortalCommissionRechargedOnlyForAgencyHeldListings_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync(rig.StandardMonth());

        var owners = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        Assert.DoesNotContain(owners.Lines, l => l.Description.StartsWith("Riaddebito commissioni Booking.com", StringComparison.Ordinal));

        await rig.Setup.SaveAccountHoldersAsync(
            rig.OrgId,
            [new ChannelAccountHolderCommand { PropertyId = rig.MountainHouse, Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency }],
            "auth0|agency");
        var agencys = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        var recharge = Assert.Single(agencys.Lines, l => l.Description.StartsWith("Riaddebito commissioni Booking.com", StringComparison.Ordinal));
        Assert.Equal((FiscalLineType.Recharge, 150.00m, 8.1m), (recharge.LineType, recharge.TotalPrice, recharge.VatRatePercent));
        Assert.Equal(owners.Id, agencys.Id);

        await rig.Setup.SaveChannelsAsync(rig.OrgId, new BillingChannelsCommand { RechargePortalCommissions = false }, null);
        var off = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        Assert.DoesNotContain(off.Lines, l => l.Description.StartsWith("Riaddebito commissioni", StringComparison.Ordinal));

        // An empty list is never «all the listings».
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Setup.SaveAccountHoldersAsync(rig.OrgId, [], null));
        Assert.Equal(BillingSetupErrors.AccountHoldersInvalid, empty.Code);
    }

    private static decimal Sum(FiscalDocument document, FiscalLineType type) => document.Lines.Where(l => l.LineType == type).Sum(l => l.TotalPrice);
}

/// <summary>
/// A Swiss agency (registered for the VAT, the catalog shipped in <c>appsettings.json</c>), one owner with two properties in Lugano and three stays, and the
/// services of the owner invoice over one in-memory database. The ledger lines of a month are written as the posting writes them, with a version cut after them.
/// </summary>
internal sealed class OwnerInvoiceRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private OwnerInvoiceRig(AppDbContext db)
    {
        Db = db;
        Clock = new FakeTimeProvider(Now);
        var fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
        Accounting = new OwnerAccountingService(db, fiscal, Clock, NullLogger<OwnerAccountingService>.Instance);
        (Documents, Invoices) = ServicesOver(db, Clock, Accounting);
        Setup = new BillingSetupService(db, Clock, NullLogger<BillingSetupService>.Instance);
    }

    /// <summary>
    /// The issue of the fiscal documents and the owner invoices over <paramref name="db"/>, with the shipped catalog of the tax rules, the issuance on, a stand-in
    /// for the Italian XML (the official schema is not in the build) and the owner accounting given (its VAT lines).
    /// </summary>
    public static (FiscalDocumentService Documents, OwnerInvoiceService Invoices) ServicesOver(AppDbContext db, TimeProvider clock, OwnerAccountingService accounting)
    {
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var issuers = new DocumentIssuerService(
            db, new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance), rules, environment.Object, clock, NullLogger<DocumentIssuerService>.Instance);
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        var documents = new FiscalDocumentService(
            db,
            issuers,
            Mock.Of<IInvoiceDataService>(),
            new FiscalNumberingService(db, clock, NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            rules,
            clock,
            NullLogger<FiscalDocumentService>.Instance,
            accounting);
        var invoices = new OwnerInvoiceService(
            db, documents, issuers, rules, new PortalAccountHolderResolver(db), new ResxDocumentTexts(), clock, NullLogger<OwnerInvoiceService>.Instance);
        return (documents, invoices);
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public OwnerAccountingService Accounting { get; }

    public FiscalDocumentService Documents { get; }

    public OwnerInvoiceService Invoices { get; }

    public BillingSetupService Setup { get; }

    public Guid OrgId { get; } = Guid.NewGuid();

    public Guid OwnerId { get; } = Guid.NewGuid();

    public Guid LakeHouse { get; } = Guid.NewGuid();

    public Guid MountainHouse { get; } = Guid.NewGuid();

    public Guid DirectStay { get; } = Guid.NewGuid();

    public Guid PortalStayLake { get; } = Guid.NewGuid();

    public Guid PortalStayMountain { get; } = Guid.NewGuid();

    /// <summary>The agency of Lugano with a complete Swiss profile (registered, UID, QR-IBAN and IBAN), its owner, two properties and three stays of October.</summary>
    public static async Task<OwnerInvoiceRig> SwissAsync()
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerInvoices_{Guid.NewGuid()}").Options,
            new OwnerInvoiceTenant(),
            new EphemeralDataProtectionProvider());
        var rig = new OwnerInvoiceRig(db);
        db.Orgs.Add(new OrgEntity
        {
            Id = rig.OrgId,
            Name = "TEST Gestioni Lugano",
            Slug = $"test-{rig.OrgId:N}"[..20],
            DisplayName = "TEST Gestioni Lugano",
            ContactEmail = "agenzia@example.com",
            PlanTier = PlanTier.Agency,
        });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = rig.OrgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = DocumentRegime.SwissVatRegistered,
            CountryCode = "CH",
            LegalName = "TEST Gestioni Lugano Sagl",
            AddressStreet = "Via del Test",
            AddressCivic = "1",
            PostalCode = "6900",
            City = "Lugano",
            VatRegistered = true,
            SwissVatNumber = "CHE-109.322.551",
            QrIban = "CH4431999123000889012",
            Iban = "CH5800791123000889012",
        });
        db.PropertyOwners.Add(new PropertyOwner
        {
            Id = rig.OwnerId,
            OrgId = rig.OrgId,
            FirstName = "TEST",
            LastName = "Proprietario",
            Email = $"{Guid.NewGuid():N}@example.com",
            EntityKind = OwnerEntityKind.NaturalPerson,
            BillingStreet = "Via dei Test",
            BillingCivic = "7",
            BillingPostalCode = "6600",
            BillingCity = "Locarno",
            BillingCountry = "CH",
            SettlementCurrency = "CHF",
        });
        db.Properties.Add(new Property { Id = rig.LakeHouse, OrgId = rig.OrgId, OwnerId = "auth0|agency", Name = "TEST Casa Lago", Address = "Via 1", City = "Lugano", Currency = "CHF" });
        db.Properties.Add(new Property { Id = rig.MountainHouse, OrgId = rig.OrgId, OwnerId = "auth0|agency", Name = "TEST Casa Monte", Address = "Via 2", City = "Lugano", Currency = "CHF" });
        var guest = new Guest { OrgId = rig.OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Guests.Add(guest);
        db.Bookings.Add(rig.Stay(rig.DirectStay, rig.LakeHouse, guest.Id, BookingSource.Direct));
        db.Bookings.Add(rig.Stay(rig.PortalStayLake, rig.LakeHouse, guest.Id, BookingSource.BookingCom));
        db.Bookings.Add(rig.Stay(rig.PortalStayMountain, rig.MountainHouse, guest.Id, BookingSource.BookingCom));
        await db.SaveChangesAsync();
        return rig;
    }

    /// <summary>
    /// The lines of October of the owner: a revenue, the commission of both properties (1'000.00 and 200.00), a cleaning flat fee (80.00), an approved charge
    /// (30.00), an expense (40.00) and the Booking.com commissions of both properties (120.00 of the lake house, 150.00 of the mountain house).
    /// </summary>
    public IReadOnlyList<OwnerLedgerEntry> StandardMonth() =>
    [
        Line(OwnerLedgerEntryType.BookingRevenue, 5000.00m, LakeHouse, OwnerLedgerSourceType.Booking, DirectStay),
        Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, LakeHouse, OwnerLedgerSourceType.Booking, DirectStay),
        Line(OwnerLedgerEntryType.AgencyCommission, -200.00m, MountainHouse, OwnerLedgerSourceType.Booking, PortalStayMountain),
        Line(OwnerLedgerEntryType.OwnerCharge, -80.00m, LakeHouse, OwnerLedgerSourceType.MandateFlatFee, DirectStay, OwnerLedgerFlatFees.Cleaning),
        Line(OwnerLedgerEntryType.OwnerCharge, -30.00m, LakeHouse, OwnerLedgerSourceType.OwnerCharge, Guid.NewGuid(), "2026-10", "TEST Sostituzione lampadina"),
        Line(OwnerLedgerEntryType.OwnerExpense, -40.00m, MountainHouse, OwnerLedgerSourceType.OwnerCharge, Guid.NewGuid(), "2026-10", "TEST Riparazione caldaia"),
        Line(OwnerLedgerEntryType.ChannelFee, -120.00m, LakeHouse, OwnerLedgerSourceType.Booking, PortalStayLake),
        Line(OwnerLedgerEntryType.ChannelFee, -150.00m, MountainHouse, OwnerLedgerSourceType.Booking, PortalStayMountain),
    ];

    /// <summary>A ledger line of the owner for October, in CHF, posted before the cut-off of the version.</summary>
    public OwnerLedgerEntry Line(
        OwnerLedgerEntryType type,
        decimal amount,
        Guid propertyId,
        OwnerLedgerSourceType source = OwnerLedgerSourceType.Booking,
        Guid? sourceId = null,
        string? reference = null,
        string? description = null) => new()
        {
            OrgId = OrgId,
            PropertyOwnerId = OwnerId,
            PropertyId = propertyId,
            EntryType = type,
            SourceType = source,
            SourceId = sourceId ?? DirectStay,
            SourceReference = reference,
            IdempotencyKey = $"test:{Guid.NewGuid():N}",
            ServiceDate = new DateOnly(2026, 10, 20),
            RecognitionDate = new DateOnly(2026, 10, 20),
            PeriodYear = 2026,
            PeriodMonth = 10,
            SharePercent = 100m,
            OriginalCurrency = "CHF",
            SourceAmount = amount,
            OriginalAmount = amount,
            StatementCurrency = "CHF",
            Amount = amount,
            Description = description,
            PostedAt = Now.UtcDateTime.AddDays(-5),
        };

    /// <summary>The lines written to the ledger and a draft version of October cut after them.</summary>
    public async Task<OwnerStatementVersion> VersionAsync(IReadOnlyList<OwnerLedgerEntry> lines)
    {
        Db.OwnerLedgerEntries.AddRange(lines);
        var version = new OwnerStatementVersion
        {
            OrgId = OrgId,
            PropertyOwnerId = OwnerId,
            PeriodYear = 2026,
            PeriodMonth = 10,
            VersionNumber = 1,
            Status = OwnerStatementVersionStatus.Draft,
            Currency = "CHF",
            CutoffAt = Now.UtcDateTime.AddDays(-1),
            Revenue = lines.Where(l => l.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(l => l.Amount),
            Commission = lines.Where(l => l.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(l => l.Amount),
            ClosingBalance = lines.Sum(l => l.Amount),
            EntryCount = lines.Count,
            EntriesSha256 = OwnerAccountingService.Hash(lines),
            CreatedAt = Now.UtcDateTime.AddDays(-1),
        };
        Db.OwnerStatementVersions.Add(version);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return version;
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    /// <summary>A stay of October with no owner share in the registry: the generation of a version reads it and posts nothing of it.</summary>
    private Booking Stay(Guid id, Guid propertyId, Guid guestId, BookingSource source) => new()
    {
        Id = id,
        OrgId = OrgId,
        PropertyId = propertyId,
        GuestId = guestId,
        CheckInDate = new DateTime(2026, 10, 17),
        CheckOutDate = new DateTime(2026, 10, 20),
        NumberOfGuests = 2,
        Status = BookingStatus.CheckedOut,
        Source = source,
        BasePrice = 500m,
        TotalPrice = 500m,
    };
}

file sealed class OwnerInvoiceTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
