using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-V2-NO-RECHARGE (decision 1 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R1): when the agency holds the portal account of a listing (V2) and does not recharge the
/// commissions of the accounts it holds, the commission of the portal is a cost of the agency and the owner is not charged it. The Swiss month of the acceptance scenario (a Booking.com stay of
/// 450.00 with a commission of 67.50, the commission of the agency at 20%, the cleaning at 80.00, the invoice of the property manager at 8.1%) closes at 266.23 instead of 198.73; V1 and V2 with
/// the recharge are what they were; a statement published before the change is never rewritten; turning the recharge off is a waiver that needs an explicit acknowledgement, recorded with
/// who and when. Every step runs through the real services over one in-memory database.
/// </summary>
public class V2WithoutRechargeTests
{
    private const string AgencyUser = "auth0|agency";

    private const string Admin = "auth0|admin";

    private static readonly ResxDocumentTexts Texts = new();

    /// <summary>
    /// PROOF TEST: money rule. V2 without the recharge: the posting writes the commission of the portal as a line the agency bears (for information only), the version takes nothing of it
    /// from the owner (the commissions of the channels 0.00, the closing 266.23 and not the 198.73 of V1), the invoice of the property manager does not recharge it (its VAT is 13.77 on the
    /// commission and the cleaning only), and the statement shows the commission as information, in no total: in the PDF (the amount the agency bears, never a deduction), the CSV and the owner portal.
    /// </summary>
    [Fact]
    public async Task V2WithoutRecharge_DoesNotChargeTheOwner_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AgencyHoldsTheBookingAccountAsync();
        await world.WaiveTheRechargeAsync();

        var month = await world.CloseOctoberAsync();

        // The ledger: no channel fee the owner is charged, the commission as a line of its own (the key of the line names its type), and the balance of the owner without it.
        var ledger = await world.LedgerAsync();
        Assert.DoesNotContain(ledger, e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        var borne = Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.ChannelFeeBorneByAgency);
        Assert.Equal(
            (-67.50m, (Guid?)world.BookingId, ChannelFeeSources.Payload, $"fee:{world.BookingId:N}:11:{world.OwnerId:N}:-6750:CHF:-6750"),
            (borne.Amount, borne.SourceId, borne.RuleVersion, borne.IdempotencyKey));
        Assert.Equal(266.23m, ledger.Where(e => OwnerLedgerBalance.Counts(e.EntryType)).Sum(e => e.Amount));

        // The version: nothing taken for the commission of the portal.
        var version = month.Version;
        Assert.Equal(
            (450.00m, 0m, -103.77m, -80.00m, 0m, 266.23m),
            (version.Revenue, version.ChannelFees, version.Commission, version.Charges, version.OpeningBalance, version.ClosingBalance));

        // The invoice of the property manager: the commission of the agency and the cleaning, no recharge of the commission of the portal.
        Assert.Equal(new[] { (FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m) }, LinesOf(month.Invoice));
        Assert.Equal((170.00m, 13.77m), (month.Invoice.TaxableBase, month.Invoice.VatAmount));

        // The statement: the commission is a line for information only, under its own words, the amount the agency bears and nothing taken from the net.
        var content = await world.Statements.GetContentAsync(world.OrgId, version.Id, "it");
        var line = Assert.Single(content.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((true, true, false, -67.50m), (line.Informational, line.AgencyHoldsPortalAccountWithoutRecharge, line.AgencyHoldsPortalAccount, line.Amount));
        Assert.Equal(OwnerStatementTotals.Of(version), content.Totals);
        Assert.Equal((0m, 266.23m, 266.23m), (content.Totals.ChannelFees, content.Totals.Period, content.Totals.ClosingBalance));
        var text = TextOf(OwnerStatementLayout.Pdf(content, Texts));
        Assert.Contains("Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario", text, StringComparison.Ordinal);
        Assert.Contains(OwnerStatementLayout.Money(67.50m, "CHF"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerStatementLayout.Money(-67.50m, "CHF"), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", text, StringComparison.Ordinal);
        Assert.DoesNotContain("riaddebitate dal gestore", text, StringComparison.Ordinal);

        // The owner portal: the flag of the commission the agency bears, and the total of the part without it.
        var part = OwnerPortalStatementSectionDto.From(OwnerStatementSection.ChannelFees, content.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((0m, true, false), (part.Total, Assert.Single(part.Lines).ChannelFeeBilledToAgencyNotRecharged, part.Lines[0].ChannelFeeRechargedByAgency));
    }

    /// <summary>
    /// PROOF TEST: money rule. V1 (the account of the owner) and V2 with the recharge (the default of V2) are what they were: the commission is a channel fee the owner is charged, with the very
    /// key the posting always wrote (so a month posted before the change is not posted again), the closings are the 198.73 and the 193.26 of the acceptance scenario, the invoice recharges the
    /// commission only in V2 (5.47 of VAT more), and the statement tells each under its own words, in the totals.
    /// </summary>
    [Fact]
    public async Task V1AndV2WithRecharge_AreUnchanged_Test()
    {
        // V1: the account of the owner.
        await using var v1 = await World.CreateAsync();
        var ownerAccount = await v1.CloseOctoberAsync();
        AssertChargedChannelFee(v1, Assert.Single(await v1.LedgerAsync(), e => OwnerLedgerBalance.IsChannelFee(e.EntryType)));
        Assert.Equal((-67.50m, 198.73m), (ownerAccount.Version.ChannelFees, ownerAccount.Version.ClosingBalance));
        Assert.Equal(new[] { (FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m) }, LinesOf(ownerAccount.Invoice));
        var v1Statement = await v1.Statements.GetContentAsync(v1.OrgId, ownerAccount.Version.Id, "it");
        var v1Line = Assert.Single(v1Statement.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((false, false, false, false), (v1Line.Informational, v1Line.AgencyHoldsPortalAccount, v1Line.AgencyHoldsPortalAccountWithoutRecharge, v1Line.AgencyAccountChargedWithoutRecharge));
        Assert.Equal((-67.50m, 198.73m), (v1Statement.Totals.ChannelFees, v1Statement.Totals.ClosingBalance));
        Assert.Contains("Trattenuta dal portale (fattura del portale a tuo nome)", TextOf(OwnerStatementLayout.Pdf(v1Statement, Texts)), StringComparison.Ordinal);

        // V2 with the recharge: the same channel fee, recharged on the invoice of the property manager.
        await using var v2 = await World.CreateAsync();
        await v2.AgencyHoldsTheBookingAccountAsync();
        var recharged = await v2.CloseOctoberAsync();
        AssertChargedChannelFee(v2, Assert.Single(await v2.LedgerAsync(), e => OwnerLedgerBalance.IsChannelFee(e.EntryType)));
        Assert.Equal((-67.50m, 193.26m), (recharged.Version.ChannelFees, recharged.Version.ClosingBalance));
        Assert.Equal(
            new[] { (FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m), (FiscalLineType.Recharge, 67.50m, 8.1m, 5.47m) },
            LinesOf(recharged.Invoice));
        var v2Statement = await v2.Statements.GetContentAsync(v2.OrgId, recharged.Version.Id, "it");
        var v2Line = Assert.Single(v2Statement.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((false, true, false, false), (v2Line.Informational, v2Line.AgencyHoldsPortalAccount, v2Line.AgencyHoldsPortalAccountWithoutRecharge, v2Line.AgencyAccountChargedWithoutRecharge));
        Assert.Equal((-67.50m, 193.26m), (v2Statement.Totals.ChannelFees, v2Statement.Totals.ClosingBalance));
        Assert.Contains("Commissioni del portale riaddebitate dal gestore (fattura n. PM2026/1)", TextOf(OwnerStatementLayout.Pdf(v2Statement, Texts)), StringComparison.Ordinal);

        static void AssertChargedChannelFee(World world, OwnerLedgerEntry fee) =>
            Assert.Equal(
                (OwnerLedgerEntryType.ChannelFee, -67.50m, $"fee:{world.BookingId:N}:2:{world.OwnerId:N}:-6750:CHF:-6750"),
                (fee.EntryType, fee.Amount, fee.IdempotencyKey));
    }

    /// <summary>
    /// PROOF TEST: data rule. A statement published before the change (the posting of then charged the commission to the owner of an account of the agency without the recharge: 198.73) is
    /// never rewritten: once the month is posted under the new rule (the channel fee reversed, the commission posted as the agency's: a new version at 266.23), the published version keeps its
    /// stored totals, its published file and hash and its stored PDF, its lines still give its totals, and it is still told as what it was (the account of the agency, no recharge on an
    /// invoice), never as a commission the owner was not charged. The ledger keeps the line it charged, reversed by one line of its own.
    /// </summary>
    [Fact]
    public async Task PublishedStatementVersions_AreNotRewritten_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AgencyHoldsTheBookingAccountAsync();
        await world.WaiveTheRechargeAsync();

        // October, closed and published before the change: the posting of then read no holder of an account (every listing as V1) and charged the commission to the owner.
        var before = await world.CloseOctoberAsync(beforeTheRule: true);
        Assert.Equal((-67.50m, 198.73m), (before.Version.ChannelFees, before.Version.ClosingBalance));
        var published = await world.PublishAsync(before.Version);
        var pdf = (await world.Statements.GetPdfAsync(world.OrgId, published.Id)).Content;
        var charged = Assert.Single(await world.LedgerAsync(), e => e.EntryType == OwnerLedgerEntryType.ChannelFee);

        // The month posted under the new rule: the channel fee is reversed, the commission posted as the agency's, and the new version takes nothing of it from the owner.
        var posting = await world.Accounting.PostPeriodAsync(world.OrgId, 2026, 10, AgencyUser);
        Assert.Equal((1, 1, 0), (posting.Posted, posting.Reversed, posting.Issues.Count(i => i.Blocking)));
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await world.Accounting.GenerateVersionAsync(world.OrgId, world.OwnerId, 2026, 10, AgencyUser);
        world.Db.ChangeTracker.Clear();
        Assert.Equal((0m, 266.23m), (after.ChannelFees, after.ClosingBalance));

        // The published version: superseded by the new one, and nothing of what was published changed.
        var stored = await world.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Id == published.Id);
        Assert.Equal(OwnerStatementVersionStatus.Superseded, stored.Status);
        Assert.Equal(
            (published.PublishedSha256, published.PublishedSnapshot, published.EntriesSha256, -67.50m, 198.73m, published.PublishedAt),
            (stored.PublishedSha256, stored.PublishedSnapshot, stored.EntriesSha256, stored.ChannelFees, stored.ClosingBalance, stored.PublishedAt));
        Assert.Equal(pdf, (await world.Statements.GetPdfAsync(world.OrgId, published.Id)).Content);

        // Its lines still give its totals (no 409), and the commission charged then is told as what it was: never as a commission the owner was not charged.
        var content = await world.Statements.GetContentAsync(world.OrgId, published.Id, "it");
        Assert.Equal(OwnerStatementTotals.Of(stored), content.Totals);
        var line = Assert.Single(content.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((-67.50m, true, false, false), (line.Amount, line.AgencyAccountChargedWithoutRecharge, line.AgencyHoldsPortalAccountWithoutRecharge, line.Informational));
        var text = TextOf(OwnerStatementLayout.Pdf(content, Texts));
        Assert.Contains("Commissioni del portale (account del portale intestato al gestore, senza riaddebito in fattura)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("non addebitata al proprietario", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", text, StringComparison.Ordinal);
        Assert.False(Assert.Single(OwnerPortalStatementSectionDto.From(OwnerStatementSection.ChannelFees, content.Of(OwnerStatementSection.ChannelFees)).Lines).ChannelFeeBilledToAgencyNotRecharged);

        // The ledger is append-only: the line charged then is still there, reversed once, and the commission is now the agency's.
        var ledger = await world.LedgerAsync();
        Assert.Equal(-67.50m, Assert.Single(ledger, e => e.Id == charged.Id).Amount);
        Assert.Equal(67.50m, Assert.Single(ledger, e => e.ReversesEntryId == charged.Id).Amount);
        Assert.Equal(-67.50m, Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.ChannelFeeBorneByAgency).Amount);
    }

    /// <summary>
    /// PROOF TEST: permission and data rule. Turning the recharge off while the agency holds a portal account is a waiver of the reimbursement the law gives the agency: without the explicit
    /// acknowledgement (left out or false) the save is refused with 422 <c>recharge_waiver_not_acknowledged</c> and nothing is written; with it the setting is saved and the activity log of the
    /// organization records, in the same save, who acknowledged it and when. A save that keeps a waiver in place asks nothing and records nothing more; turning it off again after turning it on
    /// is a new waiver. An organization with no account of the agency waives nothing by turning the recharge off, and making the agency the default holder then starts the waiver: asked too.
    /// </summary>
    [Fact]
    public async Task RechargeWaiver_NeedsTheAcknowledgement_AndRecordsWhoAndWhen_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AgencyHoldsTheBookingAccountAsync();

        // Without the acknowledgement the save is refused, with its code and its message, and nothing is written: the recharge stays on, no line in the activity log.
        foreach (var acknowledged in new bool?[] { null, false })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Setup.SaveAsync(
                world.OrgId,
                new PortalAccountSettingsCommand { RechargePortalCommissions = false, AcknowledgeRechargeWaiver = acknowledged },
                Admin));
            Assert.Equal((PortalAccountErrors.RechargeWaiverNotAcknowledged, "RechargeWaiverNotAcknowledged"), (refused.Code, refused.MessageKey));
            world.Db.ChangeTracker.Clear();
        }

        Assert.True((await world.Setup.GetAsync(world.OrgId)).RechargePortalCommissions);
        Assert.Empty(await world.Db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.NotEqual("RechargeWaiverNotAcknowledged", Texts.Get("RechargeWaiverNotAcknowledged", "it"));
        Assert.NotEqual(Texts.Get("RechargeWaiverNotAcknowledged", "it"), Texts.Get("RechargeWaiverNotAcknowledged", "en"));

        // With it: saved, and the activity log says who acknowledged the waiver and when.
        world.Clock.Advance(TimeSpan.FromMinutes(7));
        var when = world.Clock.GetUtcNow().UtcDateTime;
        var saved = await world.Setup.SaveAsync(
            world.OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false, AcknowledgeRechargeWaiver = true }, Admin);
        world.Db.ChangeTracker.Clear();
        Assert.False(saved.RechargePortalCommissions);
        var line = Assert.Single(await world.Db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.Equal(
            (world.OrgId, OrgActivityType.OrgSettingsChanged, Admin, world.OrgId.ToString(), when, "{\"part\":\"portalCommissionRechargeWaiver\"}"),
            (line.OrgId, line.Type, line.ActorUserId, line.SubjectId, line.When, line.DetailsJson));

        // The waiver in place: a save that keeps it asks nothing and records nothing more.
        await world.Setup.SaveAsync(world.OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false }, Admin);
        world.Db.ChangeTracker.Clear();
        Assert.Single(await world.Db.OrgActivityEntries.AsNoTracking().ToListAsync());

        // Turning the recharge back on asks nothing; turning it off again is a new waiver.
        await world.Setup.SaveAsync(world.OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = true }, Admin);
        world.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DomainRuleException>(() => world.Setup.SaveAsync(world.OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false }, Admin));
        world.Db.ChangeTracker.Clear();
        Assert.True((await world.Setup.GetAsync(world.OrgId)).RechargePortalCommissions);

        // No account of the agency: turning the recharge off waives nothing; making the agency the default holder then starts the waiver, and is refused without the acknowledgement.
        await using var owners = await World.CreateAsync();
        Assert.False((await owners.Setup.SaveAsync(owners.OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false }, Admin)).RechargePortalCommissions);
        owners.Db.ChangeTracker.Clear();
        Assert.Empty(await owners.Db.OrgActivityEntries.AsNoTracking().ToListAsync());
        var agencyDefault = await Assert.ThrowsAsync<DomainRuleException>(() => owners.Setup.SaveAsync(
            owners.OrgId, new PortalAccountSettingsCommand { PortalAccountHolder = PortalAccountHolder.Agency }, Admin));
        owners.Db.ChangeTracker.Clear();
        Assert.Equal(PortalAccountErrors.RechargeWaiverNotAcknowledged, agencyDefault.Code);
        Assert.Equal(PortalAccountHolder.Owner, (await owners.Setup.GetAsync(owners.OrgId)).PortalAccountHolder);
    }

    private static (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] LinesOf(FiscalDocument document) =>
        [.. document.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatRatePercent, l.VatAmount ?? 0m))];

    /// <summary>Every text of the blocks of a PDF content, one line each.</summary>
    private static string TextOf(PdfDocumentContent content) => string.Join(
        '\n',
        content.Blocks.SelectMany(block => block switch
        {
            PdfHeading heading => [heading.Text],
            PdfParagraph paragraph => [paragraph.Text],
            PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
            _ => Array.Empty<string>(),
        }));

    /// <summary>What the end of October left: the version of the month with the VAT of the invoice of the property manager, and that invoice as issued.</summary>
    private sealed record MonthEnd(OwnerStatementVersion Version, FiscalDocument Invoice);

    /// <summary>
    /// A Swiss agency of Lugano (registered for the VAT), its owner (a private person paid in francs, with a mandate of 20% and a cleaning of 80.00) and the Booking.com stay of October (450.00,
    /// a commission of 67.50 in the reservation), with the owner accounting, the invoices of the property manager, the settings of the portal accounts and the statement files over one database.
    /// <see cref="BeforeTheRule"/> is the posting as it was before FIX/BE-V2-NO-RECHARGE: it reads no holder, so it charges every commission to the owner.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private World(AppDbContext db)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
            var options = Microsoft.Extensions.Options.Options.Create(new OwnerAccountingOptions());
            Fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Mandates = new OwnerMandateService(db, Clock, options, NullLogger<OwnerMandateService>.Instance);
            Setup = new PortalAccountSettingsService(db, Clock, NullLogger<PortalAccountSettingsService>.Instance, new ActivityLog(db, Clock));
            Statements = new OwnerStatementDocuments(
                db,
                new MigraDocPdfDocumentRenderer(),
                Texts,
                new InMemoryFileStorage(),
                new OwnerFileContentScanner(),
                new TestFeatureFlags(FeatureFlags.AgencyTier),
                Mock.Of<IEmailQueue>(),
                new PublicSiteLinks(Microsoft.Extensions.Options.Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" })),
                new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
                Clock,
                NullLogger<OwnerStatementDocuments>.Instance,
                holders: new PortalAccountHolderResolver(db));
            Accounting = new OwnerAccountingService(
                db, Fiscal, Clock, NullLogger<OwnerAccountingService>.Instance, options, Statements, holders: new PortalAccountHolderResolver(db));
            BeforeTheRule = new OwnerAccountingService(db, Fiscal, Clock, NullLogger<OwnerAccountingService>.Instance, options, Statements);
            Invoices = OwnerInvoiceRig.ServicesOver(db, Clock, Accounting).Invoices;
            InvoicesBeforeTheRule = OwnerInvoiceRig.ServicesOver(db, Clock, BeforeTheRule).Invoices;
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerMandateService Mandates { get; }

        public PortalAccountSettingsService Setup { get; }

        public OwnerStatementDocuments Statements { get; }

        public OwnerAccountingService Accounting { get; }

        public OwnerAccountingService BeforeTheRule { get; }

        public OwnerInvoiceService Invoices { get; }

        public OwnerInvoiceService InvoicesBeforeTheRule { get; }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid OwnerId { get; } = Guid.NewGuid();

        public Guid PropertyId { get; } = Guid.NewGuid();

        public Guid BookingId { get; } = Guid.NewGuid();

        public static async Task<World> CreateAsync()
        {
            var world = new World(new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"V2WithoutRecharge_{Guid.NewGuid()}").Options,
                new V2WithoutRechargeTenant(),
                new EphemeralDataProtectionProvider()));
            await world.SeedAsync();
            return world;
        }

        /// <summary>The agency holds the Booking.com account of the flat (V2), written in bulk as the billing setup writes it.</summary>
        public async Task AgencyHoldsTheBookingAccountAsync()
        {
            await Setup.SaveAccountHoldersAsync(
                OrgId,
                [new ChannelAccountHolderCommand { PropertyId = PropertyId, Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency }],
                AgencyUser);
            Db.ChangeTracker.Clear();
        }

        /// <summary>The organization turns the recharge of the commissions off, acknowledging the waiver.</summary>
        public async Task WaiveTheRechargeAsync()
        {
            await Setup.SaveAsync(OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false, AcknowledgeRechargeWaiver = true }, AgencyUser);
            Db.ChangeTracker.Clear();
        }

        /// <summary>
        /// The end of October, as the agency does it: the month posted, its version generated, the invoice of the property manager made from it and issued (its VAT posted to the ledger),
        /// the version generated again with it. A minute between the steps, as between the requests of a person.
        /// </summary>
        public async Task<MonthEnd> CloseOctoberAsync(bool beforeTheRule = false)
        {
            var accounting = beforeTheRule ? BeforeTheRule : Accounting;
            var invoices = beforeTheRule ? InvoicesBeforeTheRule : Invoices;

            var posting = await accounting.PostPeriodAsync(OrgId, 2026, 10, AgencyUser);
            Assert.DoesNotContain(posting.Issues, i => i.Blocking);
            await NextRequestAsync();
            var first = await accounting.GenerateVersionAsync(OrgId, OwnerId, 2026, 10, AgencyUser);
            await NextRequestAsync();
            var draft = await invoices.CreateDraftAsync(OrgId, first.Id, AgencyUser);
            await NextRequestAsync();
            var issued = await invoices.IssueAsync(OrgId, draft.Id, confirm: true, AgencyUser);
            await NextRequestAsync();
            var version = await accounting.GenerateVersionAsync(OrgId, OwnerId, 2026, 10, AgencyUser);
            await NextRequestAsync();
            var invoice = await Db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id);
            return new MonthEnd(version, invoice);
        }

        /// <summary>The version approved and published (its PDF stored with it).</summary>
        public async Task<OwnerStatementVersion> PublishAsync(OwnerStatementVersion version)
        {
            await Accounting.ApproveVersionAsync(OrgId, version.Id, AgencyUser);
            await NextRequestAsync();
            var published = await Accounting.PublishVersionAsync(OrgId, version.Id, AgencyUser);
            await NextRequestAsync();
            return published;
        }

        /// <summary>The lines of the owner ledger of October, reversals included.</summary>
        public Task<List<OwnerLedgerEntry>> LedgerAsync() =>
            Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.PeriodYear == 2026 && e.PeriodMonth == 10).ToListAsync();

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private Task NextRequestAsync()
        {
            Db.ChangeTracker.Clear();
            Clock.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        }

        private async Task SeedAsync()
        {
            Db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Gestioni Lugano",
                Slug = $"test-{OrgId:N}"[..20],
                DisplayName = "TEST Gestioni Lugano",
                ContactEmail = "agenzia@example.com",
                PlanTier = PlanTier.Agency,
            });
            Db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                OrgId = OrgId,
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
            Db.PropertyOwners.Add(new PropertyOwner
            {
                Id = OwnerId,
                OrgId = OrgId,
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
            Db.Properties.Add(new Property { Id = PropertyId, OrgId = OrgId, OwnerId = AgencyUser, Name = "TEST Casa Lugano", Address = "Via 1", City = "Lugano", Currency = "CHF" });
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = PropertyId,
                PropertyOwnerId = OwnerId,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var guest = new Guest { OrgId = OrgId, FirstName = "TEST", LastName = "Ospite", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.Guests.Add(guest);
            Db.Bookings.Add(new Booking
            {
                Id = BookingId,
                OrgId = OrgId,
                PropertyId = PropertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.BookingCom,
                BasePrice = 450m,
                TotalPrice = 450m,
                OtaCommission = 67.50m,
            });
            await Db.SaveChangesAsync();

            // The owner: the approved Swiss rule set, its fiscal profile in francs, and its mandate (20% of the gross, the cleaning at 80.00 a stay, «+ IVA»).
            var policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
            {
                Jurisdiction = "CH",
                RuleCode = "TEST-CH",
                Version = "1",
                ValidFrom = new DateOnly(2026, 1, 1),
                SourceUrl = "https://example.com/test-rule",
                SourceSha256 = new string('d', 64),
                Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
            }, null);
            await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            await Fiscal.SaveProfileAsync(OrgId, OwnerId, new OwnerFiscalProfileCommand
            {
                ValidFrom = new DateOnly(2026, 1, 1),
                EntityKind = OwnerEntityKind.NaturalPerson,
                LegalName = "TEST Proprietario",
                TaxResidenceCountry = "CH",
                LegalAddress = "Via del Test 1, Lugano",
                ForeignTaxId = "756123456789",
                SettlementCurrency = "CHF",
                BankAccountReference = "CH9300762011623852957",
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = "CH",
                AccountingPolicyId = policy.Id,
            }, null);
            await Mandates.CreateVersionAsync(
                new HostScope(OrgId),
                OwnerId,
                new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 20m, CommissionIncludesVat = false, CleaningFlatRate = 80m },
                null);
            Db.ChangeTracker.Clear();
        }
    }
}

file sealed class V2WithoutRechargeTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
