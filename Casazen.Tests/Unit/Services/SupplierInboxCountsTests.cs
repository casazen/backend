using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: <c>GET api/supplier/inbox/counts</c>, the numbers on the chips of the list and on the menu: one place, the rules of the tabs,
/// the supplier of the caller only, and the payments that are late.
/// </summary>
public class SupplierInboxCountsTests
{
    [Fact]
    public async Task Counts_ASupplierWithNothing_AreAllZero()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var counts = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);

        Assert.Equal(new SupplierInboxCounts(0, 0, 0, 0, 0, 0), counts);
    }

    [Fact]
    public async Task Counts_SplitTheRequestsOfTheSupplierByTab()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        var counts = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);

        Assert.Equal(2, counts.ToAnswer);
        Assert.Equal(1, counts.AwaitingCustomer);
        Assert.Equal(3, counts.Scheduled); // two taken and one in progress
        Assert.Equal(3, counts.ToCollect);
        Assert.Equal(4, counts.Archive); // one paid, two rejected, one cancelled
        Assert.Equal(0, counts.PaymentsLate);
    }

    [Theory]
    [InlineData(SupplierInboxTab.ToAnswer)]
    [InlineData(SupplierInboxTab.AwaitingCustomer)]
    [InlineData(SupplierInboxTab.New)]
    [InlineData(SupplierInboxTab.Scheduled)]
    [InlineData(SupplierInboxTab.ToCollect)]
    [InlineData(SupplierInboxTab.Archive)]
    public async Task Counts_AreTheTotalOfTheTabTheyAreOn_SoAChipAndItsListNeverDisagree(SupplierInboxTab tab)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        var counts = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);
        var (_, total) = await s.Reader.ListAsync(
            s.SupplierOrgId,
            new SupplierInboxQuery(
                SupplierInboxTabs.StatusesOf(tab),
                null,
                null,
                1,
                1,
                SupplierInboxTabs.SortOf(tab),
                AwaitingCustomer: SupplierInboxTabs.AwaitingCustomerOf(tab)));

        var counted = tab switch
        {
            SupplierInboxTab.ToAnswer => counts.ToAnswer,
            SupplierInboxTab.AwaitingCustomer => counts.AwaitingCustomer,
            SupplierInboxTab.New => counts.ToAnswer + counts.AwaitingCustomer,
            SupplierInboxTab.Scheduled => counts.Scheduled,
            SupplierInboxTab.ToCollect => counts.ToCollect,
            SupplierInboxTab.Archive => counts.Archive,
            _ => throw new ArgumentOutOfRangeException(nameof(tab)),
        };
        Assert.Equal(counted, total);
        Assert.NotEqual(0, total);
    }

    [Fact]
    public async Task Counts_EveryRequestIsInExactlyOneCounter()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        var counts = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);

        var (_, everything) = await s.Reader.ListAsync(s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 1));
        Assert.Equal(everything, counts.ToAnswer + counts.AwaitingCustomer + counts.Scheduled + counts.ToCollect + counts.Archive);
    }

    [Fact]
    public async Task Counts_NeverIncludeTheRequestsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        for (var i = 0; i < 5; i++)
        {
            await s.SeedAsync(ServiceRequestStatus.Richiesto, supplierOrgId: other);
            await s.SeedAsync(ServiceRequestStatus.Completato, supplierOrgId: other);
        }

        var mine = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);
        var theirs = await s.Reader.GetInboxCountsAsync(other);

        Assert.Equal(new SupplierInboxCounts(1, 0, 0, 0, 0, 0), mine);
        Assert.Equal(new SupplierInboxCounts(5, 0, 0, 5, 0, 0), theirs);
    }

    [Fact]
    public async Task Counts_AHostOrgThatIsNotTheSupplier_HasNothing()
    {
        // The host org owns the requests it sends but is not their supplier: nothing is counted for it.
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto);

        Assert.Equal(new SupplierInboxCounts(0, 0, 0, 0, 0, 0), await s.Reader.GetInboxCountsAsync(s.HostOrgId));
    }

    // ─── The payments that are late ───

    [Fact]
    public async Task PaymentsLate_AreTheOnesFlaggedLateThatTheHostStillOwes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var late = SupplierReadModelsTestSupport.Now.AddDays(-1);
        await LatePaymentAsync(s, ServicePaymentStatus.Requested, late); // counts
        await LatePaymentAsync(s, ServicePaymentStatus.Failed, late); // counts
        await LatePaymentAsync(s, ServicePaymentStatus.Requested, lateAt: null); // asked, not late
        await LatePaymentAsync(s, ServicePaymentStatus.Processing, late); // the money is on its way
        await LatePaymentAsync(s, ServicePaymentStatus.NeedsReview, late); // waits for an admin, not for the host
        await LatePaymentAsync(s, ServicePaymentStatus.Canceled, late); // withdrawn

        var counts = await s.Reader.GetInboxCountsAsync(s.SupplierOrgId);

        Assert.Equal(2, counts.PaymentsLate);
    }

    [Fact]
    public async Task PaymentsLate_APaymentThatWasLateAndThenPaid_IsNotLateAnymore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var paid = await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato,
            created: "2026-09-01T08:00:00Z",
            taken: "2026-09-01T08:10:00Z",
            completed: "2026-09-02T10:00:00Z",
            paid: "2026-09-20T10:00:00Z",
            change: r => r.FinalAmountCents = 6000);
        await s.AddPaymentAsync(
            paid.Id,
            ServicePaymentStatus.Paid,
            requestedAt: SupplierReadModelsTestSupport.Utc("2026-09-02T10:00:00Z"),
            lateAt: SupplierReadModelsTestSupport.Utc("2026-09-09T10:00:00Z"),
            paidAt: SupplierReadModelsTestSupport.Utc("2026-09-20T10:00:00Z"));

        Assert.Equal(0, (await s.Reader.GetInboxCountsAsync(s.SupplierOrgId)).PaymentsLate);
    }

    [Fact]
    public async Task PaymentsLate_NeverIncludeThePaymentsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var theirRequest = await s.SeedCompletedAsync(supplierOrgId: other);
        await s.AddPaymentAsync(
            theirRequest.Id, ServicePaymentStatus.Requested, lateAt: SupplierReadModelsTestSupport.Now.AddDays(-1), supplierOrgId: other);

        Assert.Equal(0, (await s.Reader.GetInboxCountsAsync(s.SupplierOrgId)).PaymentsLate);
        Assert.Equal(1, (await s.Reader.GetInboxCountsAsync(other)).PaymentsLate);
    }

    [Fact]
    public async Task PaymentsLate_WithoutTheReadsOfThePayments_IsZero_NotAnError()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await LatePaymentAsync(s, ServicePaymentStatus.Requested, SupplierReadModelsTestSupport.Now.AddDays(-1));
        var reader = new SupplierServiceRequestReader(s.Db, s.Clock);

        var counts = await reader.GetInboxCountsAsync(s.SupplierOrgId);

        Assert.Equal(0, counts.PaymentsLate);
        Assert.Equal(1, counts.ToCollect);
    }

    // ─── helpers ───

    /// <summary>
    /// Two new requests to answer, one with a proposal, two taken, one in progress, three completed, one paid, two rejected and one
    /// cancelled.
    /// </summary>
    private static async Task SeedAsync(ServiceRequestScenario s)
    {
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        await s.SeedAsync(
            ServiceRequestStatus.Richiesto,
            r =>
            {
                r.ProposedStartUtc = ServiceRequestScenario.FridayAt14;
                r.ProposedEndUtc = ServiceRequestScenario.FridayAt14.AddHours(2);
                r.ProposedAt = SupplierReadModelsTestSupport.Now;
            });
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.InCorso, r => r.TakenAt = r.CreatedAt);
        for (var i = 0; i < 3; i++)
            await s.SeedAsync(ServiceRequestStatus.Completato, r => r.CompletedAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.Pagato, r => r.PaidAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.Rifiutato);
        await s.SeedAsync(ServiceRequestStatus.Rifiutato);
        await s.SeedAsync(ServiceRequestStatus.Annullato, r => r.CancelledAt = r.CreatedAt);
    }

    /// <summary>A completed request with a payment in <paramref name="status"/>, flagged late at <paramref name="lateAt"/> (not when null).</summary>
    private static async Task LatePaymentAsync(ServiceRequestScenario s, ServicePaymentStatus status, DateTime? lateAt)
    {
        var request = await s.SeedCompletedAsync(change: r => r.PaymentMode = ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, status, lateAt: lateAt);
    }
}
