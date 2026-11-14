using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// E06/F01/US02/T05 (ADR-019 amended, AC-NF110-6), rewritten by E14/F01/US01/T01 (decision P1 of 2026-11-14): the owner statement is still a non-fiscal versioned
/// report, and the invoice of the property manager made from it is the fiscal document; the old generate endpoint makes those invoices instead of answering
/// 410; the invoices of the retired generator stay readable and are never called compliant; a link to an invoice the agency's external system issued changes
/// nothing of the statement and is itself evidence.
/// </summary>
public class InvoiceProofTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    /// <summary>
    /// PROOF TEST: data rule. A published statement has no fiscal series, number, XML or SDI state and publishing it makes no fiscal document; the owner invoice
    /// made from it is one (its purpose, owner, period and version, a number of the owner-invoice series, the XML of an Italian invoice), and issuing it after the
    /// publication changes nothing of the published statement: the readiness tells the agency to generate the month again.
    /// </summary>
    [Fact]
    public async Task OwnerStatement_IsNoFiscalDocument_TheOwnerInvoiceIs_Test()
    {
        var world = await InvoiceWorld.CreateAsync();
        var version = await world.PublishedVersionAsync();

        Assert.Equal(OwnerStatementVersionStatus.Published, version.Status);
        Assert.Empty(await world.Db.FiscalDocuments.ToListAsync());
        Assert.Empty(await world.Db.FiscalNumberSequences.ToListAsync());
        Assert.Empty(await world.Db.AgencyInvoices.ToListAsync());
        Assert.DoesNotContain("FatturaElettronica", version.PublishedSnapshot!, StringComparison.Ordinal);

        // A version has no fiscal series, number, XML or SDI state: only its own version number.
        var names = typeof(OwnerStatementVersion).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Xml", StringComparison.OrdinalIgnoreCase) || n.Contains("Series", StringComparison.Ordinal)
            || n.Contains("Sdi", StringComparison.Ordinal) || (n.Contains("Number", StringComparison.Ordinal) && n != nameof(OwnerStatementVersion.VersionNumber)));
        Assert.Equal("Rendiconto gestionale - non è una fattura", new ResxDocumentTexts().Get("StatementDocNonFiscal", "it"));

        // The owner invoice made from the statement is the fiscal document.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var invoices = world.OwnerInvoices();
        var draft = await invoices.CreateDraftAsync(OrgId, version.Id, "auth0|agency");
        var issued = await invoices.IssueAsync(OrgId, draft.Id, confirm: true, "auth0|agency");
        Assert.Equal(
            (FiscalDocumentPurpose.OwnerInvoice, (Guid?)world.Owner, (Guid?)version.Id, (int?)2026, (int?)10),
            (issued.Purpose, issued.RecipientOwnerId, issued.OwnerStatementVersionId, issued.PeriodYear, issued.PeriodMonth));
        Assert.Equal(("PM2026/1", FiscalDocumentSdiStatuses.ToSend), (issued.Number, issued.SdiStatus));
        Assert.False(string.IsNullOrEmpty(issued.FatturaPaXml));
        Assert.Equal((10.00m, 2.20m, 12.20m), (issued.TaxableBase, issued.VatAmount, issued.Total));

        // The published statement is not touched; its month is told to be generated again, naming the invoice.
        var stored = await world.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        Assert.Equal((OwnerStatementVersionStatus.Published, version.PublishedSha256, version.ClosingBalance), (stored.Status, stored.PublishedSha256, stored.ClosingBalance));
        var readiness = await world.Service.GetReadinessAsync(OrgId, 2026, 10);
        Assert.Contains(
            readiness.Owners.Single(o => o.OwnerId == world.Owner).Issues,
            i => i.Code == OwnerReadinessCodes.OwnerInvoiceAfterApproval && i.SourceId == issued.Id);
    }

    /// <summary>
    /// PROOF TEST: data rule. The old generate (<c>POST api/agency-invoices/generate</c>) no longer answers 410: for a period it makes the draft of every owner with
    /// a version, and makes it again on a second run; the rows of the retired generator are never written and stay readable as they were stored, unverified.
    /// </summary>
    [Fact]
    public async Task LegacyAgencyInvoice_GenerateMakesOwnerInvoiceDraftsAndHistoryRemainsReadable_Test()
    {
        var world = await InvoiceWorld.CreateAsync();
        var version = await world.DraftVersionAsync();
        const string Xml = "<p:FatturaElettronica versione=\"FPR12\">TEST storico</p:FatturaElettronica>";
        var legacy = new AgencyInvoice
        {
            OrgId = OrgId,
            OwnerId = world.Owner,
            InvoiceNumber = "7",
            Series = "A",
            Month = 9,
            Year = 2026,
            TaxableBase = 100m,
            VatRate = 22m,
            VatAmount = 22m,
            Total = 122m,
            Status = InvoiceStatus.ToBeIssuedManually,
            FatturapaXml = Xml,
            CreatedAt = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc),
        };
        world.Db.AgencyInvoices.Add(legacy);
        await world.Db.SaveChangesAsync();
        var service = new AgencyInvoiceService(world.Db);
        var invoices = world.OwnerInvoices();

        var generation = await invoices.CreateDraftsForPeriodAsync(OrgId, 2026, 10, "auth0|agency");
        Assert.Equal((1, 1, 0, 0, 0), (generation.Owners, generation.Created, generation.Updated, generation.AlreadyIssued, generation.Failed.Count));
        var draft = Assert.Single(await world.Db.FiscalDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(
            (FiscalDocumentPurpose.OwnerInvoice, FiscalDocumentStatus.Draft, (Guid?)version.Id, (Guid?)world.Owner),
            (draft.Purpose, draft.Status, draft.OwnerStatementVersionId, draft.RecipientOwnerId));
        var again = await invoices.CreateDraftsForPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((0, 1), (again.Created, again.Updated));
        Assert.Single(await world.Db.FiscalDocuments.AsNoTracking().ToListAsync());
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => invoices.CreateDraftsForPeriodAsync(OrgId, 2026, 13, null));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalid.Code);

        // The history is read as it was stored, and says it is unverified; nothing wrote to it.
        Assert.Equal(Xml, await service.GetInvoiceXmlAsync(legacy.Id));
        var copy = Encoding.UTF8.GetString(await service.GetInvoicePdfAsync(legacy.Id));
        Assert.Contains("A7", copy, StringComparison.Ordinal);
        Assert.Contains("Date: 2026-09-30", copy, StringComparison.Ordinal);
        Assert.Contains(AgencyInvoiceCompliance.LegacyUnverified, copy, StringComparison.Ordinal);
        var stored = await world.Db.AgencyInvoices.AsNoTracking().SingleAsync();
        Assert.Equal((AgencyInvoiceCompliance.LegacyUnverified, 122m, "7"), (stored.ComplianceStatus, stored.Total, stored.InvoiceNumber));
    }

    [Fact]
    public async Task ExternalInvoiceReference_CannotChangeDocumentAmountsOrStatus_Test()
    {
        var world = await InvoiceWorld.CreateAsync();
        var draft = await world.DraftVersionAsync();
        var command = new OwnerExternalDocumentCommand
        {
            Provider = "TEST Gestionale",
            ExternalId = "ext-2026-15",
            ExternalNumber = "2026/15",
            ExternalDate = new DateOnly(2026, 11, 5),
            DocumentUrl = "https://example.com/fatture/2026-15.pdf",
            DocumentSha256 = new string('d', 64),
        };

        // Only an approved or published statement is linked, with valid facts.
        var early = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.AddExternalDocumentAsync(OrgId, draft.Id, command, null));
        Assert.Equal(OwnerAccountingErrors.VersionNotApproved, early.Code);
        await world.Service.ApproveVersionAsync(OrgId, draft.Id, null);
        var insecure = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.AddExternalDocumentAsync(OrgId, draft.Id, command with { DocumentUrl = "http://example.com/x.pdf" }, null));
        Assert.Equal("documentUrl", insecure.Extensions["field"]);

        var before = await world.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Id == draft.Id);
        var reference = await world.Service.AddExternalDocumentAsync(OrgId, draft.Id, command, "auth0|finance");
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.AddExternalDocumentAsync(OrgId, draft.Id, command, null));

        // The link has no amount, VAT, number of CasaZen or submission state, and the statement is untouched.
        var names = typeof(OwnerExternalDocumentReference).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.Ordinal) || n.Contains("Vat", StringComparison.Ordinal)
            || n.Contains("Total", StringComparison.Ordinal) || n.Contains("Submi", StringComparison.Ordinal) || n.Contains("Sdi", StringComparison.Ordinal));
        var after = await world.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Id == draft.Id);
        Assert.Equal(
            (before.Status, before.ClosingBalance, before.Revenue, before.EntriesSha256, before.VersionNumber),
            (after.Status, after.ClosingBalance, after.Revenue, after.EntriesSha256, after.VersionNumber));

        // Its facts never change and it is never deleted: verification and removal are recorded on it.
        world.Db.ChangeTracker.Clear();
        var tracked = await world.Db.OwnerExternalDocumentReferences.SingleAsync();
        tracked.ExternalNumber = "2026/16";
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Db.SaveChangesAsync());
        world.Db.ChangeTracker.Clear();
        world.Db.OwnerExternalDocumentReferences.Remove(await world.Db.OwnerExternalDocumentReferences.SingleAsync());
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Db.SaveChangesAsync());
        world.Db.ChangeTracker.Clear();

        var verified = await world.Service.VerifyExternalDocumentAsync(OrgId, reference.Id, "auth0|finance");
        Assert.Equal(OwnerExternalDocumentStatus.Verified, verified.Status);

        // The owner sees the link only once the statement is published, and never a removed one.
        var ownerUser = await world.PortalUserAsync();
        Assert.Empty(await world.Service.ListPublishedExternalDocumentsForOwnerUserAsync(ownerUser));
        await world.Service.PublishVersionAsync(OrgId, draft.Id, null);
        Assert.Single(await world.Service.ListPublishedExternalDocumentsForOwnerUserAsync(ownerUser));
        var removed = await world.Service.RemoveExternalDocumentAsync(OrgId, reference.Id, "TEST numero sbagliato", "auth0|finance");
        Assert.NotNull(removed.RemovedAt);
        Assert.Empty(await world.Service.ListPublishedExternalDocumentsForOwnerUserAsync(ownerUser));
        var kept = Assert.Single(await world.Service.ListExternalDocumentsAsync(OrgId, draft.Id));
        Assert.Equal(("2026/15", "TEST numero sbagliato"), (kept.ExternalNumber, kept.RemovalReason));
    }

    /// <summary>
    /// An agency with one owner and one property paid in October, an Italian issuer of the ordinary regime, and the owner accounting over one in-memory
    /// database.
    /// </summary>
    private sealed class InvoiceWorld
    {
        private Guid _property;

        private InvoiceWorld(AppDbContext db)
        {
            Db = db;
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
            Fiscal = new OwnerFiscalProfileService(db, clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Service = new OwnerAccountingService(db, Fiscal, clock, NullLogger<OwnerAccountingService>.Instance);
            Clock = clock;
        }

        public AppDbContext Db { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerAccountingService Service { get; }

        public FakeTimeProvider Clock { get; }

        /// <summary>The owner of the version (after <see cref="DraftVersionAsync"/>).</summary>
        public Guid Owner { get; private set; }

        public static async Task<InvoiceWorld> CreateAsync()
        {
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Invoices_{Guid.NewGuid()}").Options,
                new InvoiceTenant(),
                new EphemeralDataProtectionProvider());
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Agenzia",
                Slug = "test-agenzia-" + Guid.NewGuid().ToString("N"),
                DisplayName = "TEST Agenzia",
                ContactEmail = "agenzia@example.com",
                PlanTier = PlanTier.Agency,
                HasPartitaIva = true,
                PartitaIvaNumber = "01234567897",
                FiscalCode = "01234567897",
            });
            db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                OrgId = OrgId,
                IssuerMode = DocumentIssuerMode.CasaZen,
                DocumentRegime = DocumentRegime.VatOrdinary,
                LegalName = "TEST Agenzia Srl",
                AddressStreet = "Via di Test",
                AddressCivic = "1",
                PostalCode = "00187",
                City = "Roma",
                Province = "RM",
                CountryCode = "IT",
            });
            await db.SaveChangesAsync();
            return new InvoiceWorld(db);
        }

        /// <summary>The invoices of the property manager to the owners over this database and clock.</summary>
        public OwnerInvoiceService OwnerInvoices() => OwnerInvoiceRig.ServicesOver(Db, Clock, Service).Invoices;

        public async Task<Guid> OwnerAsync()
        {
            var owner = new PropertyOwner
            {
                OrgId = OrgId,
                FirstName = "TEST",
                LastName = "Owner",
                Email = $"{Guid.NewGuid():N}@example.com",
                BillingStreet = "Via del Proprietario",
                BillingCivic = "3",
                BillingPostalCode = "00100",
                BillingCity = "Roma",
                BillingProvince = "RM",
                BillingCountry = "IT",
            };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            return owner.Id;
        }

        /// <summary>A draft version of October: one payment of 100, the owner's profile, the approved Italian rule set and a mandate of 10% «+ IVA».</summary>
        public async Task<OwnerStatementVersion> DraftVersionAsync()
        {
            Owner = await OwnerAsync();
            var policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
            {
                Jurisdiction = "IT",
                RuleCode = "TEST-IT",
                Version = "1",
                ValidFrom = new DateOnly(2026, 1, 1),
                SourceUrl = "https://example.com/test-rule",
                SourceSha256 = new string('e', 64),
                Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
            }, null);
            await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            await Fiscal.SaveProfileAsync(OrgId, Owner, new OwnerFiscalProfileCommand
            {
                ValidFrom = new DateOnly(2026, 1, 1),
                EntityKind = OwnerEntityKind.NaturalPerson,
                LegalName = "TEST Proprietario",
                TaxResidenceCountry = "IT",
                LegalAddress = "Via del Test 1",
                FiscalCode = "RSSMRA70A01H501W",
                SettlementCurrency = "EUR",
                BankAccountReference = "IT60X0542811101000000123456",
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = "IT",
                AccountingPolicyId = policy.Id,
            }, null);

            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 10, 10),
                CheckOutDate = new DateTime(2026, 10, 12),
                NumberOfGuests = 2,
                Status = BookingStatus.CheckedOut,
            };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = Owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2026, 1, 1),
            });
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.Payments.Add(new Payment
            {
                OrgId = OrgId,
                BookingId = booking.Id,
                Amount = 100m,
                Status = PaymentStatus.Completed,
                TransactionId = "TEST-1",
                Description = "TEST",
                ProcessedAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc),
            });

            // A stay of an owner without a mandate holds its version back (E13/F01/US01/T02): the owner has one of 10%, so the owner invoice has a commission.
            Db.OwnerMandates.Add(new OwnerMandate { OrgId = OrgId, PropertyOwnerId = Owner, CommissionPercent = 10m, ValidFrom = new DateOnly(2026, 1, 1) });
            await Db.SaveChangesAsync();
            _property = property.Id;

            await Service.PostPeriodAsync(OrgId, 2026, 10, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            return await Service.GenerateVersionAsync(OrgId, Owner, 2026, 10, null);
        }

        public async Task<OwnerStatementVersion> PublishedVersionAsync()
        {
            var version = await DraftVersionAsync();
            await Service.ApproveVersionAsync(OrgId, version.Id, null);
            return await Service.PublishVersionAsync(OrgId, version.Id, null);
        }

        /// <summary>The owner as a user of the portal: its ownership names the portal user.</summary>
        public async Task<Guid> PortalUserAsync()
        {
            var ownerUser = Guid.NewGuid();
            var ownership = await Db.PropertyOwnerships.SingleAsync(o => o.PropertyId == _property && o.PropertyOwnerId == Owner);
            ownership.OwnerUserId = ownerUser;
            await Db.SaveChangesAsync();
            return ownerUser;
        }
    }
}

file sealed class InvoiceTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
