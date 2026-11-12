using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F05/US02/T02 (NF-110, AC-NF110-1..3, ADR-017): the owner ledger is append-only and reproduces every statement amount, a published statement never
/// changes (a later version supersedes it), a month closes only reconciled to the paid wires, and the shares and exchange rates in force are applied
/// so that the owners' parts add up to the source.
/// </summary>
public class OwnerAccountingCloseTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    [Fact]
    public async Task OwnerLedger_PostedEntryCannotBeMutated_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR");
        var property = await world.PropertyAsync("EUR", (owner, 100m, new DateTime(2026, 1, 1)));
        await world.MandateAsync(property, owner, 20m);
        var payment = await world.PaymentAsync(property, 100m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc));

        var first = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");
        Assert.Equal(2, first.Posted);
        var again = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");
        Assert.Equal((0, 0, 2), (again.Posted, again.Reversed, again.Unchanged));

        // A posted line is neither edited nor deleted: the save is refused before anything is written.
        var revenue = await world.Db.OwnerLedgerEntries.SingleAsync(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue);
        revenue.Amount = 1m;
        var edit = await Assert.ThrowsAsync<DomainConflictException>(() => world.Db.SaveChangesAsync());
        Assert.Equal(OwnerAccountingErrors.LedgerImmutable, edit.Code);
        world.Db.ChangeTracker.Clear();
        world.Db.OwnerLedgerEntries.Remove(await world.Db.OwnerLedgerEntries.SingleAsync(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission));
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Db.SaveChangesAsync());
        world.Db.ChangeTracker.Clear();

        // The source changes (a partial refund): the old lines stay as they were, a reversal and a replacement are posted.
        var tracked = await world.Db.Payments.SingleAsync(p => p.Id == payment);
        tracked.RefundedAmount = 30m;
        await world.Db.SaveChangesAsync();
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var corrected = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");
        Assert.Equal((2, 2), (corrected.Posted, corrected.Reversed));

        var lines = await world.Db.OwnerLedgerEntries.AsNoTracking().ToListAsync();
        Assert.Equal(6, lines.Count);
        var original = lines.Single(e => e.Id == revenue.Id);
        Assert.Equal(100m, original.Amount);
        var reversal = lines.Single(e => e.ReversesEntryId == revenue.Id);
        Assert.Equal(-100m, reversal.Amount);
        Assert.Equal(56m, lines.Sum(e => e.Amount));
        Assert.All(lines, e => Assert.Equal("TEST-IT@1", e.RuleVersion));
    }

    [Fact]
    public async Task OwnerStatement_PublishedChangeCreatesSupersedingVersion_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR");
        var property = await world.PropertyAsync("EUR", (owner, 100m, new DateTime(2026, 1, 1)));
        await world.MandateAsync(property, owner, 20m);
        await world.PaymentAsync(property, 100m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var v1 = await world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, "auth0|admin");
        Assert.Equal((1, OwnerStatementVersionStatus.Draft, 80m), (v1.VersionNumber, v1.Status, v1.ClosingBalance));
        await world.Service.ApproveVersionAsync(OrgId, v1.Id, "auth0|admin");
        var published = await world.Service.PublishVersionAsync(OrgId, v1.Id, "auth0|publisher");
        var file = published.PublishedSnapshot!;
        Assert.Equal(Sha256(file), published.PublishedSha256);
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, null));

        // A published version never changes.
        world.Db.ChangeTracker.Clear();
        var stored = await world.Db.OwnerStatementVersions.SingleAsync(v => v.Id == v1.Id);
        stored.ClosingBalance = 0m;
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Db.SaveChangesAsync());
        world.Db.ChangeTracker.Clear();

        // A payment after the cut-off makes the month dirty: the published version cannot be acted on, a new version supersedes it.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.PaymentAsync(property, 50m, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.True((await world.Db.OwnerClosePeriods.AsNoTracking().SingleAsync()).IsDirty);
        var dirty = await Assert.ThrowsAsync<DomainConflictException>(
            () => world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: true, null));
        Assert.Equal(OwnerAccountingErrors.PeriodDirty, dirty.Code);

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var v2 = await world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, "auth0|admin");
        Assert.Equal((2, v1.Id, 120m), (v2.VersionNumber, v2.SupersedesVersionId, v2.ClosingBalance));
        var old = await world.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id);
        Assert.Equal(OwnerStatementVersionStatus.Superseded, old.Status);
        Assert.Equal(v2.Id, old.SupersededByVersionId);
        Assert.Equal(file, old.PublishedSnapshot);
        Assert.Equal(80m, old.ClosingBalance);
        Assert.False((await world.Db.OwnerClosePeriods.AsNoTracking().SingleAsync()).IsDirty);

        // Every amount of the new version is the ledger's: its lines hash to it and add up to its balance.
        var detail = await world.Service.GetVersionAsync(OrgId, v2.Id);
        Assert.Equal(v2.EntriesSha256, OwnerAccountingService.Hash(detail.Entries));
        Assert.Equal(v2.ClosingBalance, v2.OpeningBalance + detail.Entries.Sum(e => e.Amount));
        Assert.Equal((150m, -30m), (v2.Revenue, v2.Commission));
    }

    [Fact]
    public async Task OwnerClose_UnreconciledPayoutCannotClose_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR");
        var property = await world.PropertyAsync("EUR", (owner, 100m, new DateTime(2026, 1, 1)));
        await world.MandateAsync(property, owner, 20m);
        await world.PaymentAsync(property, 100m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var version = await world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, null);

        var draft = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: true, null));
        Assert.Equal(OwnerAccountingErrors.VersionNotApproved, draft.Code);
        await world.Service.ApproveVersionAsync(OrgId, version.Id, null);

        // Nothing was paid for the 80 due: no close without a waiver, and a waiver needs the finance permission.
        var unreconciled = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: true, null));
        Assert.Equal(OwnerAccountingErrors.CloseUnreconciled, unreconciled.Code);
        Assert.Equal(80m, unreconciled.Extensions["difference"]);
        var forbidden = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand { WaiverReason = "TEST pagato in contanti" }, mayWaive: false, null));
        Assert.Equal(OwnerAccountingErrors.CloseWaiverForbidden, forbidden.Code);
        world.Db.ChangeTracker.Clear();
        Assert.Equal(OwnerClosePeriodStatus.Open, (await world.Db.OwnerClosePeriods.AsNoTracking().SingleAsync()).Status);

        // A wire of 60 paid for the month still leaves 20: refused; the wire of the rest reconciles and closes.
        await world.PaidWireAsync(owner, property, 60m);
        var partial = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: true, null));
        Assert.Equal(20m, partial.Extensions["difference"]);
        await world.PaidWireAsync(owner, property, 20m);
        var closed = await world.Service.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: false, "auth0|admin");
        Assert.Equal((OwnerClosePeriodStatus.Closed, 80m, 80m, 0m), (closed.Status, closed.DueAmount!.Value, closed.PaidAmount!.Value, closed.Difference!.Value));
        Assert.Null(closed.WaiverReason);
        Assert.Equal(new DateOnly(2036, 12, 31), closed.RetainUntil);
        Assert.False(OwnerRetention.MayPurge(closed, new DateOnly(2036, 12, 31)));
        Assert.True(OwnerRetention.MayPurge(closed, new DateOnly(2037, 1, 1)));

        // A closed month is never posted or versioned again.
        await world.PaymentAsync(property, 40m, new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc));
        var after = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(0, after.Posted);
        var reopen = await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, null));
        Assert.Equal(OwnerAccountingErrors.PeriodClosed, reopen.Code);

        // The legal hold keeps the evidence past its retention.
        var held = await world.Service.SetLegalHoldAsync(OrgId, owner, 2026, 10, true, "TEST contenzioso", null);
        Assert.False(OwnerRetention.MayPurge(held, new DateOnly(2040, 1, 1)));
        held.LegalHold = false;
        Assert.True(OwnerRetention.MayPurge(held, new DateOnly(2040, 1, 1)));
    }

    [Fact]
    public async Task OwnerLedger_MultiOwnerEffectiveShareAndFxReconcile_Test()
    {
        var world = await World.CreateAsync();
        var swiss = await world.OwnerAsync("CH", "CHF");
        var italian = await world.OwnerAsync("IT", "EUR");
        // The Swiss owner alone until 14 October, then 60/40 with the Italian owner.
        var property = await world.PropertyAsync(
            "CHF",
            (swiss, 100m, new DateTime(2026, 1, 1)),
            (swiss, 60m, new DateTime(2026, 10, 15)),
            (italian, 40m, new DateTime(2026, 10, 15)));
        await world.EndOwnershipAsync(property, swiss, 100m, new DateTime(2026, 10, 15));
        world.Db.OwnerFxRates.Add(new OwnerFxRate
        {
            OrgId = OrgId, BaseCurrency = "CHF", QuoteCurrency = "EUR", RateDate = new DateOnly(2026, 10, 17), Rate = 1.0650m, Source = "TEST BCE",
        });
        await world.Db.SaveChangesAsync();

        var alone = await world.PaymentAsync(property, 1000m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc));
        var shared = await world.PaymentAsync(property, 333.33m, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
        var stale = await world.PaymentAsync(property, 100m, new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.Db.OwnerLedgerEntries.AsNoTracking().ToListAsync();

        // Before the change the Swiss owner had it all; after it the parts add up to the payment, the last owner taking the remainder.
        Assert.Equal(100m, lines.Single(e => e.SourceId == alone).SharePercent);
        var parts = lines.Where(e => e.SourceId == shared).ToList();
        Assert.Equal(333.33m, parts.Sum(e => e.OriginalAmount));
        Assert.All(parts, e => Assert.Equal(333.33m, e.SourceAmount));
        var swissPart = parts.Single(e => e.PropertyOwnerId == swiss);
        var italianPart = parts.Single(e => e.PropertyOwnerId == italian);
        Assert.Equal((60m, "CHF", 1m), (swissPart.SharePercent, swissPart.StatementCurrency, swissPart.FxRate));
        Assert.Equal((40m, "EUR", 1.0650m, "TEST BCE"), (italianPart.SharePercent, italianPart.StatementCurrency, italianPart.FxRate, italianPart.FxSource));
        Assert.Equal(new DateOnly(2026, 10, 17), italianPart.FxDate);
        Assert.Equal(Math.Round(italianPart.OriginalAmount * italianPart.FxRate, 2, MidpointRounding.ToEven), italianPart.Amount);
        Assert.Equal(swissPart.OriginalAmount, swissPart.Amount);
        Assert.Equal("TEST-CH@1", swissPart.RuleVersion);
        Assert.Equal("TEST-IT@1", italianPart.RuleVersion);

        // A rate older than 7 days is not used: the Italian part of the last payment is not posted and the month says why.
        Assert.Single(lines, e => e.SourceId == stale);
        Assert.Contains(result.Issues, i => i.Code == OwnerReadinessCodes.FxMissing && i.OwnerId == italian && i.SourceId == stale);
        var readiness = await world.Service.GetReadinessAsync(OrgId, 2026, 10);
        Assert.Contains(readiness.Owners.Single(o => o.OwnerId == italian).Issues, i => i.Code == OwnerReadinessCodes.FxMissing);

        // Each owner's version is its own lines: the Swiss one is clean, the Italian one is flagged until the rate is entered.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var swissVersion = await world.Service.GenerateVersionAsync(OrgId, swiss, 2026, 10, null);
        var italianVersion = await world.Service.GenerateVersionAsync(OrgId, italian, 2026, 10, null);
        Assert.Equal((OwnerStatementVersionStatus.Draft, "CHF"), (swissVersion.Status, swissVersion.Currency));
        Assert.Equal(lines.Where(e => e.PropertyOwnerId == swiss).Sum(e => e.Amount), swissVersion.ClosingBalance);
        Assert.Equal((OwnerStatementVersionStatus.Flagged, "EUR"), (italianVersion.Status, italianVersion.Currency));
        Assert.Equal(italianPart.Amount, italianVersion.ClosingBalance);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>An org with its owners, properties and payments, and the services over one in-memory database.</summary>
    private sealed class World
    {
        private World(AppDbContext db, FakeTimeProvider clock)
        {
            Db = db;
            Clock = clock;
            Fiscal = new OwnerFiscalProfileService(db, clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Service = new OwnerAccountingService(db, Fiscal, clock, NullLogger<OwnerAccountingService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerAccountingService Service { get; }

        public static Task<World> CreateAsync()
        {
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerLedger_{Guid.NewGuid()}").Options,
                new LedgerCloseTenant(),
                new EphemeralDataProtectionProvider());
            return Task.FromResult(new World(db, new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero))));
        }

        /// <summary>An owner with a complete profile from January and the approved rule set of its jurisdiction.</summary>
        public async Task<Guid> OwnerAsync(string jurisdiction, string currency)
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = jurisdiction, Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();

            var policies = await Fiscal.ListPoliciesAsync(OrgId);
            var policy = policies.FirstOrDefault(p => p.Jurisdiction == jurisdiction);
            if (policy is null)
            {
                policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
                {
                    Jurisdiction = jurisdiction,
                    RuleCode = "TEST-" + jurisdiction,
                    Version = "1",
                    ValidFrom = new DateOnly(2026, 1, 1),
                    SourceUrl = "https://example.com/test-rule",
                    SourceSha256 = new string('c', 64),
                    Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
                }, null);
                await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            }

            await Fiscal.SaveProfileAsync(OrgId, owner.Id, new OwnerFiscalProfileCommand
            {
                ValidFrom = new DateOnly(2026, 1, 1),
                EntityKind = OwnerEntityKind.NaturalPerson,
                LegalName = "TEST Proprietario " + jurisdiction,
                TaxResidenceCountry = jurisdiction,
                LegalAddress = "Via del Test 1",
                FiscalCode = "RSSMRA70A01H501W",
                SettlementCurrency = currency,
                BankAccountReference = "IT60X0542811101000000123456",
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = jurisdiction,
                AccountingPolicyId = policy.Id,
            }, null);
            return owner.Id;
        }

        public async Task<Guid> PropertyAsync(string currency, params (Guid Owner, decimal Share, DateTime From)[] owners)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = currency };
            Db.Properties.Add(property);
            foreach (var (owner, share, from) in owners)
                Db.PropertyOwnerships.Add(new PropertyOwnership { OrgId = OrgId, PropertyId = property.Id, PropertyOwnerId = owner, SharePercent = share, OwnershipStart = from });
            await Db.SaveChangesAsync();
            return property.Id;
        }

        public async Task EndOwnershipAsync(Guid property, Guid owner, decimal share, DateTime end)
        {
            var row = await Db.PropertyOwnerships.SingleAsync(o => o.PropertyId == property && o.PropertyOwnerId == owner && o.SharePercent == share);
            row.OwnershipEnd = end;
            await Db.SaveChangesAsync();
        }

        public async Task MandateAsync(Guid property, Guid owner, decimal commission)
        {
            var ownership = await Db.PropertyOwnerships.SingleAsync(o => o.PropertyId == property && o.PropertyOwnerId == owner);
            Db.OwnerMandates.Add(new OwnerMandate
            {
                OrgId = OrgId,
                PropertyOwnershipId = ownership.Id,
                CommissionPercent = commission,
                MandatePdfUrl = "https://example.com/mandato-test.pdf",
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await Db.SaveChangesAsync();
        }

        public async Task<Guid> PaymentAsync(Guid property, decimal amount, DateTime paidAt)
        {
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = guest.Id,
                CheckInDate = paidAt.Date,
                CheckOutDate = paidAt.Date.AddDays(2),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
            };
            var payment = new Payment
            {
                OrgId = OrgId,
                BookingId = booking.Id,
                Amount = amount,
                Status = PaymentStatus.Completed,
                TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
                Description = "TEST",
                ProcessedAt = paidAt,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            return payment.Id;
        }

        /// <summary>A legacy statement of October for the owner paid by a wire batch marked paid.</summary>
        public async Task PaidWireAsync(Guid owner, Guid property, decimal amount)
        {
            var statement = new OwnerStatement
            {
                OrgId = OrgId, PropertyOwnerId = owner, PropertyId = property, Month = 10, Year = 2026, Currency = "EUR", Status = StatementStatus.Published,
                NetPayable = amount,
            };
            var batch = new WireBatch
            {
                OrgId = OrgId, Currency = "EUR", DebitAccount = "IT60X0542811101000000654321", TotalAmount = amount, TransferCount = 1,
                Status = WireBatchStatus.Paid, PaidAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc),
            };
            Db.OwnerStatements.Add(statement);
            Db.WireBatches.Add(batch);
            Db.WireBatchStatements.Add(new WireBatchStatement { OrgId = OrgId, WireBatchId = batch.Id, OwnerStatementId = statement.Id });
            await Db.SaveChangesAsync();
        }
    }
}

file sealed class LedgerCloseTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
