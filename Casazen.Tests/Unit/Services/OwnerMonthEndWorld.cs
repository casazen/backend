using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Tests.Unit.Services;

internal sealed class OwnerMonthEndWorld
{
    public const string UserId = "auth0|test-agency";

    private OwnerMonthEndWorld(AppDbContext db, Guid orgId, FakeTimeProvider clock, IOwnerStatementDocuments? files)
    {
        Db = db;
        OrgId = orgId;
        Clock = clock;
        var defaults = new OwnerAccountingOptions();
        foreach (var (channel, percent) in new[] { ("BookingCom", 15m), ("Expedia", 18m), ("Airbnb", 15.5m), ("Vrbo", 8m) })
            defaults.ChannelCommissionDefaults[channel] = percent;
        var options = Microsoft.Extensions.Options.Options.Create(defaults);
        Fiscal = new OwnerFiscalProfileService(db, clock, NullLogger<OwnerFiscalProfileService>.Instance);
        Service = new OwnerAccountingService(
            db, Fiscal, clock, NullLogger<OwnerAccountingService>.Instance, options, files, holders: new PortalAccountHolderResolver(db));
        Reconciliation = new OwnerReconciliationService(db, NullLogger<OwnerReconciliationService>.Instance);
    }

    public AppDbContext Db { get; }

    public Guid OrgId { get; }

    public FakeTimeProvider Clock { get; }

    public OwnerFiscalProfileService Fiscal { get; }

    public OwnerAccountingService Service { get; }

    public OwnerReconciliationService Reconciliation { get; }

    public static Task<OwnerMonthEndWorld> CreateAsync(IOwnerStatementDocuments? files = null, OwnerMonthEndWorld? sameDatabaseAs = null)
    {
        var db = sameDatabaseAs?.Db ?? new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerMonthEnd_{Guid.NewGuid()}").Options,
            new MonthEndTenant(),
            TestDataProtection.Shared);
        var clock = sameDatabaseAs?.Clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
        return Task.FromResult(new OwnerMonthEndWorld(db, Guid.NewGuid(), clock, files));
    }

    public static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    public async Task<Guid> OwnerAsync(string jurisdiction = "CH", string currency = "CHF")
    {
        var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
        Db.PropertyOwners.Add(owner);
        await Db.SaveChangesAsync();

        var policy = (await Fiscal.ListPoliciesAsync(OrgId)).FirstOrDefault(p => p.Jurisdiction == jurisdiction);
        if (policy is null)
        {
            policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
            {
                Jurisdiction = jurisdiction,
                RuleCode = "TEST-" + jurisdiction,
                Version = "1",
                ValidFrom = new DateOnly(2026, 1, 1),
                SourceUrl = "https://example.com/test-rule",
                SourceSha256 = new string('d', 64),
                Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
            }, null);
            await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
        }

        await Fiscal.SaveProfileAsync(OrgId, owner.Id, new OwnerFiscalProfileCommand
        {
            ValidFrom = new DateOnly(2026, 1, 1),
            EntityKind = OwnerEntityKind.NaturalPerson,
            LegalName = "TEST Proprietario",
            TaxResidenceCountry = jurisdiction,
            LegalAddress = "Via del Test 1",
            FiscalCode = jurisdiction == "IT" ? "RSSMRA70A01H501W" : null,
            ForeignTaxId = jurisdiction == "IT" ? null : "756123456789",
            SettlementCurrency = currency,
            BankAccountReference = jurisdiction == "IT" ? "IT60X0542811101000000123456" : "CH9300762011623852957",
            RevenueRecognitionRule = "cash",
            TaxJurisdiction = jurisdiction,
            AccountingPolicyId = policy.Id,
        }, null);
        return owner.Id;
    }

    public async Task<Guid> OwnerWithoutProfileAsync()
    {
        var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Senza profilo", Email = $"{Guid.NewGuid():N}@example.com" };
        Db.PropertyOwners.Add(owner);
        await Db.SaveChangesAsync();
        return owner.Id;
    }

    public async Task<Guid> PropertyAsync(string currency, params (Guid Owner, decimal Share)[] owners)
    {
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = currency == "CHF" ? "Lugano" : "Como", Currency = currency };
        Db.Properties.Add(property);
        foreach (var (owner, share) in owners)
        {
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = share,
                OwnershipStart = new DateTime(2026, 1, 1),
            });
        }

        await Db.SaveChangesAsync();
        return property.Id;
    }

    public async Task MandateAsync(Guid owner, decimal commission)
    {
        Db.OwnerMandates.Add(new OwnerMandate
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            CommissionPercent = commission,
            MandatePdfUrl = "https://example.com/mandato-test.pdf",
            ValidFrom = new DateOnly(2026, 1, 1),
        });
        await Db.SaveChangesAsync();
    }

    public async Task<Guid> StayAsync(
        Guid property,
        BookingSource source,
        decimal gross,
        DateTime checkIn,
        DateTime checkOut,
        decimal touristTax = 0m,
        decimal? otaCommission = null,
        decimal? otaNet = null,
        bool? companyCollect = null,
        BookingStatus status = BookingStatus.Confirmed)
    {
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            BasePrice = gross,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = gross + touristTax,
            OtaCommission = otaCommission,
            OtaNetAmount = otaNet,
            OtaCompanyCollect = companyCollect,
        };
        Db.Guests.Add(guest);
        Db.Bookings.Add(booking);
        await Db.SaveChangesAsync();
        return booking.Id;
    }

    public async Task<Guid> PaymentAsync(Guid booking, decimal amount, DateTime paidAt)
    {
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = booking,
            Amount = amount,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.BankTransfer,
            TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
            Description = "TEST",
            ProcessedAt = paidAt,
        };
        Db.Payments.Add(payment);
        await Db.SaveChangesAsync();
        return payment.Id;
    }

    public async Task PaidWireAsync(Guid owner, Guid property, decimal amount, string currency = "CHF")
    {
        var statement = new OwnerStatement
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            PropertyId = property,
            Month = 10,
            Year = 2026,
            Currency = currency,
            Status = StatementStatus.Published,
            NetPayable = amount,
        };
        var batch = new WireBatch
        {
            OrgId = OrgId,
            Currency = currency,
            DebitAccount = "CH9300762011623852957",
            TotalAmount = amount,
            TransferCount = 1,
            Status = WireBatchStatus.Paid,
            PaidAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc),
        };
        Db.OwnerStatements.Add(statement);
        Db.WireBatches.Add(batch);
        Db.WireBatchStatements.Add(new WireBatchStatement { OrgId = OrgId, WireBatchId = batch.Id, OwnerStatementId = statement.Id });
        await Db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<OwnerBulkOwnerResult>> BulkAsync(OwnerBulkAction action, params Guid[] owners)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));
        Db.ChangeTracker.Clear();
        return await Service.RunBulkAsync(OrgId, 2026, 10, action, owners, UserId);
    }

    public async Task<OwnerStatementVersion> VersionAsync(Guid owner)
    {
        Db.ChangeTracker.Clear();
        return await Db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.PropertyOwnerId == owner && v.Status != OwnerStatementVersionStatus.Superseded)
            .OrderByDescending(v => v.VersionNumber)
            .FirstAsync();
    }
}

file sealed class MonthEndTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
