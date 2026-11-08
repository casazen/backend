using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Condominium;

public class TenantCondominiumSettlementServiceTests
{
    [Fact]
    public async Task Post_AddsASettlementComponent_AndLeavesRentAndAdvance()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var condominiums = Condo(db);
        var settlements = Settlements(db, condominiums);
        await LinkAndBudgetAsync(condominiums, scope, condo, period, lease.PropertyId);
        await condominiums.ConfirmFinalAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(1920m, PriorYearBalanceDisposition.CarryForward, Recoverable(1920m)),
            "user");
        var open = await db.RentLedgerEntries.SingleAsync(entry => entry.Status == RentLedgerStatus.Scheduled);
        var paid = await db.RentLedgerEntries.SingleAsync(entry => entry.Status == RentLedgerStatus.Paid);
        var rentBefore = open.Components.Single(c => c.Kind == RentLedgerComponentKind.Rent).Amount;
        var advanceBefore = open.Components.Single(c => c.Kind == RentLedgerComponentKind.CondominiumAdvance).Amount;

        var draft = await settlements.CalculateAsync(scope, lease.Id, new CalculateTenantSettlementCommand(period.Id, null, null, null), "user");
        Assert.Equal(1620m, draft.SettlementAmount);
        Assert.Equal(TenantSettlementDirection.Debit, draft.Direction);
        Assert.Equal(300m, draft.AdvancesAccountedAmount);
        Assert.Null(draft.SentToTenantAt);
        var confirmed = await settlements.ConfirmAsync(scope, lease.Id, draft.Id, "user");
        var posted = await settlements.PostAsync(scope, lease.Id, confirmed.Id, "user");
        var again = await settlements.PostAsync(scope, lease.Id, confirmed.Id, "user");

        Assert.Equal(posted.Id, again.Id);
        Assert.Equal(1, await db.RentLedgerComponents.CountAsync(c => c.Kind == RentLedgerComponentKind.CondominiumSettlement && c.TenantSettlementId == posted.Id));
        var refreshed = await db.RentLedgerEntries.Include(e => e.Components).SingleAsync(e => e.Id == open.Id);
        Assert.Equal(rentBefore, refreshed.Components.Single(c => c.Kind == RentLedgerComponentKind.Rent).Amount);
        Assert.Equal(advanceBefore, refreshed.Components.Single(c => c.Kind == RentLedgerComponentKind.CondominiumAdvance).Amount);
        Assert.Equal(1620m, refreshed.Components.Single(c => c.Kind == RentLedgerComponentKind.CondominiumSettlement).Amount);
        Assert.Equal(refreshed.AmountDue, refreshed.Components.Sum(c => c.Amount));
        var paidAfter = await db.RentLedgerEntries.Include(e => e.Components).SingleAsync(e => e.Id == paid.Id);
        Assert.Equal(paid.AmountDue, paidAfter.AmountDue);
        Assert.DoesNotContain(paidAfter.Components, c => c.Kind == RentLedgerComponentKind.CondominiumSettlement);
        Assert.Null(posted.SentToTenantAt);
    }

    [Fact]
    public async Task NewConsuntivo_ReversesThePostedSettlement_Once()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var condominiums = Condo(db);
        var settlements = Settlements(db, condominiums);
        await LinkAndBudgetAsync(condominiums, scope, condo, period, lease.PropertyId);
        await condominiums.ConfirmFinalAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(1920m, PriorYearBalanceDisposition.CarryForward, Recoverable(1920m)),
            "user");
        var first = await settlements.CalculateAsync(scope, lease.Id, new CalculateTenantSettlementCommand(period.Id, null, null, null), "user");
        await settlements.ConfirmAsync(scope, lease.Id, first.Id, "user");
        await settlements.PostAsync(scope, lease.Id, first.Id, "user");

        await condominiums.ConfirmFinalAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(2000m, PriorYearBalanceDisposition.CarryForward, Recoverable(2000m)),
            "user");
        var replaced = await settlements.ReplacePostedForPeriodAsync(scope, period.Id, "user");
        var again = await settlements.ReplacePostedForPeriodAsync(scope, period.Id, "user");

        var current = Assert.Single(replaced);
        Assert.Equal(1700m, current.SettlementAmount);
        Assert.Equal(current.Id, Assert.Single(again).Id);
        Assert.Equal(TenantSettlementStatus.Superseded, (await db.TenantCondominiumSettlements.SingleAsync(s => s.Id == first.Id)).Status);
        Assert.Equal(1, await db.RentLedgerComponents.CountAsync(c => c.TenantSettlementId != null && c.RentLedgerEntry.Status != RentLedgerStatus.Cancelled));
    }

    [Fact]
    public async Task NeedsReview_IsNotCharged_AndAnotherOrgCannotSeeTheLease()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var condominiums = Condo(db);
        var settlements = Settlements(db, condominiums);
        await LinkAndBudgetAsync(condominiums, scope, condo, period, lease.PropertyId);
        await condominiums.ConfirmFinalAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(
                100m,
                PriorYearBalanceDisposition.CarryForward,
                [new FinalExpenseLineCommand("Da rivedere", 100m, CondominiumExpenseNature.Ordinary, TenantRecoverability.NeedsReview, null, null, ExpenseClassificationSource.Manual, null, null, ExpenseReviewStatus.Pending)]),
            "user");

        var draft = await settlements.CalculateAsync(
            scope, lease.Id, new CalculateTenantSettlementCommand(period.Id, TenantSettlementDisposition.CarryForward, null, null), "user");
        Assert.Equal(0m, draft.RecoverableFinalAmount);
        Assert.Equal(-300m, draft.SettlementAmount);
        Assert.Equal(TenantSettlementDirection.Credit, draft.Direction);

        var other = new HostScope(Guid.NewGuid());
        await Assert.ThrowsAsync<NotFoundException>(() => settlements.CalculateAsync(
            other, lease.Id, new CalculateTenantSettlementCommand(period.Id, null, null, null), "user"));
    }

    [Fact]
    public async Task Proposal_UsesTheBudget_OrTheTypedStatementsWhenNothingIsLoaded()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var condominiums = Condo(db);
        var proposals = new TenantExpenseProposalService(db, condominiums);

        var typed = await proposals.ProposeAsync(
            lease.Id, new ProposeTenantExpenseCommand(null, null, RentCadence.Monthly, 1800m, 1920m, null));
        Assert.Equal(TenantExpenseProposal.Source.Statements, typed.Source);
        Assert.Equal(160m, typed.InstallmentAmount);
        Assert.True(typed.SettledAtConsuntivo);

        await LinkAndBudgetAsync(condominiums, scope, condo, period, lease.PropertyId);
        var fromBudget = await proposals.ProposeAsync(
            lease.Id, new ProposeTenantExpenseCommand(CondominiumAdvanceMode.BudgetAllocation, null, RentCadence.Monthly, null, null, null));
        Assert.Equal(TenantExpenseProposal.Source.ConfirmedBudget, fromBudget.Source);
        Assert.Equal(100m, fromBudget.AnnualAmount);

        var fixedAdvance = await proposals.ProposeAsync(
            lease.Id, new ProposeTenantExpenseCommand(CondominiumAdvanceMode.FixedContractAmount, 150m, RentCadence.Monthly, null, null, null));
        Assert.Equal(150m, fixedAdvance.InstallmentAmount);
        Assert.Equal(TenantExpenseProposal.Source.FixedContract, fixedAdvance.Source);
    }

    [Fact]
    public async Task TotalOnlyCorrection_KeepsRecoverability_AndDoesNotCreditEveryAdvance()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var settlements = Settlements(db, Condo(db));
        await LinkAndBudgetAsync(Condo(db), scope, condo, period, lease.PropertyId);
        await settlements.ConfirmFinalAndReplaceAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(1920m, PriorYearBalanceDisposition.CarryForward, Recoverable(1920m)),
            "user");
        var first = await settlements.CalculateAsync(scope, lease.Id, new CalculateTenantSettlementCommand(period.Id, null, null, null), "user");
        await settlements.ConfirmAsync(scope, lease.Id, first.Id, "user");
        await settlements.PostAsync(scope, lease.Id, first.Id, "user");

        await settlements.ConfirmFinalAndReplaceAsync(
            scope, condo.Id, period.Id, new ConfirmFinalCommand(2000m, PriorYearBalanceDisposition.CarryForward), "user");

        var line = await db.CondominiumExpenseLines
            .SingleAsync(item => item.Statement.Status == CondominiumStatementStatus.Confirmed && item.Statement.Kind == CondominiumStatementKind.Final);
        Assert.Equal(TenantRecoverability.Recoverable, line.TenantRecoverability);
        Assert.Equal(2000m, line.Amount);
        var current = await db.TenantCondominiumSettlements.SingleAsync(item => item.Status == TenantSettlementStatus.Posted);
        Assert.Equal(1700m, current.SettlementAmount);
        Assert.NotEqual(TenantSettlementDirection.Credit, current.Direction);
    }

    [Fact]
    public async Task Replacement_KeepsExplicitLineAmounts()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var settlements = Settlements(db, Condo(db));
        await LinkAndBudgetAsync(Condo(db), scope, condo, period, lease.PropertyId);
        await settlements.ConfirmFinalAndReplaceAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(
                500m,
                PriorYearBalanceDisposition.CarryForward,
                [new FinalExpenseLineCommand("Calore", 500m, CondominiumExpenseNature.Ordinary, TenantRecoverability.Recoverable, null, null, ExpenseClassificationSource.Manual, null, null, ExpenseReviewStatus.Accepted)]),
            "user");
        var lineId = await db.CondominiumExpenseLines
            .Where(item => item.Statement.Status == CondominiumStatementStatus.Confirmed
                && item.Statement.Kind == CondominiumStatementKind.Final)
            .Select(item => item.Id)
            .SingleAsync();
        var draft = await settlements.CalculateAsync(
            scope,
            lease.Id,
            new CalculateTenantSettlementCommand(
                period.Id,
                TenantSettlementDisposition.NextInstallment,
                TenantAllocationRule.ExplicitAmount,
                [new ExplicitSettlementLine(lineId, 400m)]),
            "user");
        Assert.Equal(100m, draft.SettlementAmount);
        await settlements.ConfirmAsync(scope, lease.Id, draft.Id, "user");
        await settlements.PostAsync(scope, lease.Id, draft.Id, "user");

        await settlements.ConfirmFinalAndReplaceAsync(
            scope, condo.Id, period.Id, new ConfirmFinalCommand(800m, PriorYearBalanceDisposition.CarryForward), "user");

        var current = await db.TenantCondominiumSettlements.SingleAsync(item => item.Status == TenantSettlementStatus.Posted);
        Assert.Equal(100m, current.SettlementAmount);
        Assert.Equal(TenantSettlementDirection.Debit, current.Direction);
    }

    [Fact]
    public async Task Replacement_WithUnmatchedExplicitLines_DoesNotCreditEveryAdvance()
    {
        await using var db = NewDb();
        var (scope, lease, period, condo) = await SeedAsync(db);
        var settlements = Settlements(db, Condo(db));
        await LinkAndBudgetAsync(Condo(db), scope, condo, period, lease.PropertyId);
        await settlements.ConfirmFinalAndReplaceAsync(
            scope, condo.Id, period.Id,
            new ConfirmFinalCommand(
                500m,
                PriorYearBalanceDisposition.CarryForward,
                [new FinalExpenseLineCommand("Calore", 500m, CondominiumExpenseNature.Ordinary, TenantRecoverability.Recoverable, null, null, ExpenseClassificationSource.Manual, null, null, ExpenseReviewStatus.Accepted)]),
            "user");
        var lineId = await db.CondominiumExpenseLines
            .Where(item => item.Statement.Status == CondominiumStatementStatus.Confirmed
                && item.Statement.Kind == CondominiumStatementKind.Final)
            .Select(item => item.Id)
            .SingleAsync();
        var draft = await settlements.CalculateAsync(
            scope,
            lease.Id,
            new CalculateTenantSettlementCommand(
                period.Id,
                TenantSettlementDisposition.NextInstallment,
                TenantAllocationRule.ExplicitAmount,
                [new ExplicitSettlementLine(lineId, 400m)]),
            "user");
        await settlements.ConfirmAsync(scope, lease.Id, draft.Id, "user");
        await settlements.PostAsync(scope, lease.Id, draft.Id, "user");

        await settlements.ConfirmFinalAndReplaceAsync(
            scope,
            condo.Id,
            period.Id,
            new ConfirmFinalCommand(
                800m,
                PriorYearBalanceDisposition.CarryForward,
                [new FinalExpenseLineCommand("Acqua", 800m, CondominiumExpenseNature.Ordinary, TenantRecoverability.Recoverable, null, null, ExpenseClassificationSource.Manual, null, null, ExpenseReviewStatus.Accepted)]),
            "user");

        var current = await db.TenantCondominiumSettlements.SingleAsync(item => item.Status == TenantSettlementStatus.Posted);
        Assert.Equal(draft.Id, current.Id);
        Assert.Equal(100m, current.SettlementAmount);
        Assert.Equal(TenantSettlementDirection.Debit, current.Direction);
    }

    private static IReadOnlyList<FinalExpenseLineCommand> Recoverable(decimal amount) =>
    [
        new("Consuntivo recuperabile", amount, CondominiumExpenseNature.Ordinary, TenantRecoverability.Recoverable, null, null, ExpenseClassificationSource.Manual, null, null, ExpenseReviewStatus.Accepted),
    ];

    private static async Task LinkAndBudgetAsync(
        CondominiumAccountingService condominiums, HostScope scope, CondominiumView condo, CondominiumPeriodView period, Guid propertyId)
    {
        await condominiums.LinkPropertyAsync(scope, condo.Id, new LinkPropertyCommand(propertyId, null, null, null, null), "user");
        await condominiums.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(100m, "Preventivo", null, CondominiumInstallmentCadence.Single), "user");
    }

    private static TenantCondominiumSettlementService Settlements(AppDbContext db, CondominiumAccountingService condominiums) =>
        new(db, condominiums, NullLogger<TenantCondominiumSettlementService>.Instance);

    private static CondominiumAccountingService Condo(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(HostScope Scope, LeaseContract Lease, CondominiumPeriodView Period, CondominiumView Condo)> SeedAsync(AppDbContext db)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Demo", Slug = "demo-" + Guid.NewGuid().ToString("N")[..8] };
        var property = new Property { OrgId = org.Id, OwnerId = "user", Name = "Casa", Address = "Via Test 1", City = "Milano" };
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            Property = property,
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
        };
        var schedule = new RentSchedule
        {
            OrgId = org.Id,
            LeaseContract = lease,
            Amount = 1050m,
            BillingDayOfMonth = 1,
            Cadence = RentCadence.Monthly,
            IsActive = true,
            NextRunDate = new DateOnly(2025, 6, 1),
        };
        db.Add(org);
        db.Add(property);
        db.Add(lease);
        db.Add(schedule);
        db.Add(Entry(org.Id, lease, schedule, new DateOnly(2025, 6, 1), RentLedgerStatus.Paid));
        db.Add(Entry(org.Id, lease, schedule, new DateOnly(2025, 7, 1), RentLedgerStatus.Scheduled));
        await db.SaveChangesAsync();
        var scope = new HostScope(org.Id);
        var condominiums = Condo(db);
        var condo = await condominiums.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 6), "user");
        var period = await condominiums.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2024/2025"), "user");
        return (scope, lease, period, condo);
    }

    private static RentLedgerEntry Entry(Guid orgId, LeaseContract lease, RentSchedule schedule, DateOnly start, RentLedgerStatus status)
    {
        var entry = new RentLedgerEntry
        {
            OrgId = orgId,
            LeaseContract = lease,
            RentSchedule = schedule,
            PeriodStart = start,
            PeriodEnd = start.AddMonths(1).AddDays(-1),
            DueDate = start,
            AmountDue = 1050m,
            Status = status,
            PaidOn = status == RentLedgerStatus.Paid ? start : null,
        };
        entry.Components.Add(Part(orgId, entry, RentLedgerComponentKind.Rent, 900m));
        entry.Components.Add(Part(orgId, entry, RentLedgerComponentKind.CondominiumAdvance, 150m));
        return entry;
    }

    private static RentLedgerComponent Part(Guid orgId, RentLedgerEntry entry, RentLedgerComponentKind kind, decimal amount) =>
        new()
        {
            OrgId = orgId,
            RentLedgerEntry = entry,
            Kind = kind,
            Amount = amount,
            CompetenceStart = new DateOnly(2024, 6, 1),
            CompetenceEnd = new DateOnly(2025, 5, 31),
        };
}
