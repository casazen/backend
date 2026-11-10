using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class BudgetRentAdvanceUpdaterTests
{
    [Theory]
    [InlineData(CondominiumAdvanceMode.BudgetAllocation, 1200.01)]
    [InlineData(CondominiumAdvanceMode.FixedContractAmount, 0)]
    public async Task ConfirmedBudgetKeepsCentsAndNeverOverridesFixedContract(CondominiumAdvanceMode mode, double expectedAdvance)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = new OrgEntity { Name = "Locatore", Slug = "budget-test" };
        var property = new Property { OrgId = org.Id, OwnerId = "owner", Name = "Casa", Address = "Roma", City = "Roma" };
        var lease = new LeaseContract { OrgId = org.Id, PropertyId = property.Id, MonthlyRent = 900,
            StartDate = new(2026, 1, 1), EndDate = new(2029, 12, 31) };
        var schedule = new RentSchedule { OrgId = org.Id, LeaseContractId = lease.Id, IsActive = true, Cadence = RentCadence.Monthly, Amount = 900 };
        var terms = new LeaseCondominiumTerms { OrgId = org.Id, LeaseContractId = lease.Id, AdvanceMode = mode, ConfirmedAt = DateTime.UtcNow };
        var condo = new Condominium { OrgId = org.Id, Name = "Condominio" };
        var unit = new CondominiumUnit { OrgId = org.Id, CondominiumId = condo.Id, PropertyId = property.Id };
        var period = new CondominiumPeriod { OrgId = org.Id, CondominiumId = condo.Id, Label = "2026", StartDate = new(2026, 1, 1), EndDate = new(2026, 12, 31) };
        var budget = new CondominiumStatement { OrgId = org.Id, CondominiumPeriodId = period.Id, Kind = CondominiumStatementKind.Budget,
            Lines = [new() { OrgId = org.Id, CondominiumUnitId = unit.Id, Description = "Ordinario", RecoverableAmount = 1200.01m,
                TenantRecoverability = TenantRecoverability.Recoverable, ExpenseNature = CondominiumExpenseNature.Ordinary, ReviewStatus = ExpenseReviewStatus.Accepted },
                new() { OrgId = org.Id, CondominiumUnitId = unit.Id, Description = "Da verificare", RecoverableAmount = 9999,
                    TenantRecoverability = TenantRecoverability.NeedsReview, ReviewStatus = ExpenseReviewStatus.Pending }] };
        db.AddRange(org, property, lease, schedule, terms, condo, unit, period, budget);
        for (var month = 1; month <= 12; month++)
            db.RentLedgerEntries.Add(new RentLedgerEntry { OrgId = org.Id, LeaseContractId = lease.Id, RentScheduleId = schedule.Id,
                PeriodStart = new(2026, month, 1), PeriodEnd = new(2026, month, DateTime.DaysInMonth(2026, month)), AmountDue = 900 });
        await db.SaveChangesAsync();
        await BudgetRentAdvanceUpdater.RefreshAsync(db, lease.Id, default);
        await BudgetRentAdvanceUpdater.RefreshAsync(db, lease.Id, default);
        Assert.Equal((decimal)expectedAdvance, await db.RentLedgerComponents.Where(c => c.Kind == RentLedgerComponentKind.CondominiumAdvance).SumAsync(c => c.Amount));
        Assert.Equal(10800m + (decimal)expectedAdvance, await db.RentLedgerEntries.SumAsync(e => e.AmountDue));
    }
}
