using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class TenantRentSepaServiceTests
{
    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero); }
    private sealed class Fixture : IDisposable
    {
        public readonly AppDbContext Db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly Mock<IStripeRentSepaGateway> Gateway = new();
        public readonly Mock<IStripeService> Stripe = new();
        public readonly LeaseContract Lease;
        public readonly RentSchedule Schedule;
        public readonly RentLedgerEntry Entry;
        public readonly TenantRentSepaService Service;
        public Fixture()
        {
            var org = new Org { Name = "Locatore", Slug = "owner", StripeConnectedAccountId = "acct_owner", ConnectChargesEnabled = true };
            var property = new Casazen.Core.Entities.Property { OrgId = org.Id, OwnerId = "owner", Name = "Casa", Address = "Via Roma", City = "Roma" };
            Lease = new()
            {
                OrgId = org.Id,
                PropertyId = property.Id,
                Status = LeaseStatus.Registered,
                StartDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                MonthlyRent = 1000
            };
            Schedule = new() { OrgId = org.Id, LeaseContractId = Lease.Id, IsActive = true, Cadence = RentCadence.Monthly, Amount = 1100 };
            Entry = new()
            {
                OrgId = org.Id,
                LeaseContractId = Lease.Id,
                RentScheduleId = Schedule.Id,
                PeriodStart = new(2026, 11, 1),
                PeriodEnd = new(2026, 11, 30),
                DueDate = new(2026, 11, 10),
                AmountDue = 1100,
                Components = [new() { OrgId = org.Id, Kind = RentLedgerComponentKind.Rent, Amount = 1000 },
                    new() { OrgId = org.Id, Kind = RentLedgerComponentKind.CondominiumAdvance, Amount = 100 }]
            };
            Db.AddRange(org, property, Lease, Schedule, Entry); Db.SaveChanges();
            Gateway.Setup(g => g.CreateAsync("acct_owner", Lease.Id, Entry.Id, 110100, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Intent());
            Stripe.Setup(g => g.GetPaymentIntentAsync("pi_rent", null, It.IsAny<CancellationToken>())).ReturnsAsync(Intent());
            Service = new(Db, Gateway.Object, Stripe.Object, new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<TenantRentSepaService>.Instance, Mock.Of<IRentBillingService>(), new Clock());
        }
        public PaymentIntent Intent(string status = "requires_payment_method") => new()
        {
            Id = "pi_rent",
            Amount = 110100,
            ClientSecret = "pi_rent_secret",
            Status = status,
            TransferData = new PaymentIntentTransferData { DestinationId = "acct_owner" }
        };
        public void Dispose() => Db.Dispose();
    }
    [Fact]
    public async Task ManualQuoteIncludesRentAdvanceAndOneFee_AndReusesSameIntent()
    {
        using var f = new Fixture();
        var first = await f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, f.Entry.Id);
        var again = await f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, f.Entry.Id);
        Assert.Equal(first, again); Assert.Equal("", first.StripeAccountId);
        Assert.Equal(1100m, f.Entry.AmountDue); Assert.Equal(1m, f.Entry.TransactionFee);
        Assert.Null(f.Entry.ConnectedAccountId);
        f.Gateway.Verify(g => g.CreateAsync("acct_owner", f.Lease.Id, f.Entry.Id, 110100, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(f.Schedule.SepaAutomaticEnabled);
    }
    [Fact]
    public async Task SavedMandateIsOnlyOfferedToItsAuthenticatedHolder()
    {
        using var f = new Fixture();
        f.Schedule.SepaAuthorizedByUserId = "tenant"; f.Schedule.SepaAuthorizedAt = DateTime.UtcNow;
        f.Schedule.SepaPaymentMethodId = "pm_sepa"; f.Schedule.SepaCustomerId = "cus_tenant";
        f.Schedule.LandlordStripeAccountId = "acct_owner"; await f.Db.SaveChangesAsync();
        f.Gateway.Setup(g => g.AttachAsync("pi_rent", "cus_tenant", "pm_sepa", It.IsAny<CancellationToken>())).ReturnsAsync(f.Intent());
        var result = await f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, f.Entry.Id, userId: "tenant");
        Assert.True(result.UseSavedMandate);
        Assert.False(f.Schedule.SepaAutomaticEnabled);
        f.Gateway.Verify(g => g.AttachAsync("pi_rent", "cus_tenant", "pm_sepa", It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task ProcessingCannotBePaidManuallyAgain()
    {
        using var f = new Fixture(); f.Entry.Status = RentLedgerStatus.Processing; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, f.Entry.Id));
        f.Gateway.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task TenantCannotPayAnotherOrganizationInstallment()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.PayAsync(Guid.NewGuid(), f.Lease.Id, f.Entry.Id));
        f.Gateway.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task AutomaticDebitsOnce_ManualAndSecondJobCannotDuplicate()
    {
        using var f = new Fixture();
        f.Schedule.SepaAutomaticEnabled = true; f.Schedule.SepaAutomaticFrom = new(2026, 11, 10);
        f.Schedule.SepaPaymentMethodId = "pm_sepa"; f.Schedule.SepaCustomerId = "cus_tenant";
        f.Schedule.LandlordStripeAccountId = "acct_owner";
        await f.Db.SaveChangesAsync();
        f.Gateway.Setup(g => g.ConfirmAsync("acct_owner", "pi_rent", "cus_tenant", "pm_sepa", It.IsAny<CancellationToken>())).ReturnsAsync(f.Intent("processing"));
        f.Stripe.Setup(g => g.GetPaymentIntentAsync("pi_rent", null, It.IsAny<CancellationToken>())).ReturnsAsync(f.Intent("processing"));
        await f.Service.RunAsync();
        await f.Service.RunAsync();
        var entry = await f.Db.RentLedgerEntries.SingleAsync();
        Assert.Equal(RentLedgerStatus.Processing, entry.Status);
        await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, entry.Id));
        f.Gateway.Verify(g => g.ConfirmAsync("acct_owner", "pi_rent", "cus_tenant", "pm_sepa", It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task EarlyEndDisablesAutomaticAndRejectsNewPayment()
    {
        using var f = new Fixture();
        f.Lease.CurrentTermEndDate = f.Lease.TerminatedOn = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        f.Schedule.SepaAutomaticEnabled = true; await f.Db.SaveChangesAsync();
        await f.Service.RunAsync();
        Assert.False((await f.Db.RentSchedules.SingleAsync()).SepaAutomaticEnabled);
        await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.PayAsync(f.Lease.OrgId, f.Lease.Id, f.Entry.Id));
        f.Gateway.VerifyNoOtherCalls();
    }
}
