using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: the tabs <c>da-rispondere</c> and <c>in-attesa</c> split the new requests by what the supplier already did (it proposed
/// another time and the customer has not answered), <c>nuove</c> stays what it was (the Golden Journey and the other users do not
/// change), and every request carries the <c>phase</c> the server derives once. Scenario clock: Thursday 8 October 2026, 12:00 in Rome.
/// </summary>
public class SupplierInboxTabsPhaseTests
{
    // ─── The tabs ───

    [Fact]
    public async Task ToAnswerTab_HoldsTheNewRequestsWithNoProposal_TheMostUrgentFirst_TheOnesWithoutDeadlineLast()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var later = await NewAsync(s, minutesToExpire: 90);
        var sooner = await NewAsync(s, minutesToExpire: 15);
        var noDeadline = await NewAsync(s, minutesToExpire: null);
        await ProposedAsync(s, minutesToExpire: 5);
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.Rifiutato);

        var items = await TabAsync(s, SupplierInboxTab.ToAnswer);

        Assert.Equal(new[] { sooner, later, noDeadline }, items.Select(item => item.Id));
        Assert.All(items, item => Assert.Equal(SupplierRequestPhases.ToAnswer, item.Phase));
        Assert.All(items, item => Assert.Null(item.Proposal));
    }

    [Fact]
    public async Task AwaitingCustomerTab_HoldsTheNewRequestsWithAProposalTheCustomerHasNotAnswered_TheMostUrgentFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var later = await ProposedAsync(s, minutesToExpire: 90);
        var sooner = await ProposedAsync(s, minutesToExpire: 15);
        await NewAsync(s, minutesToExpire: 1);
        await s.SeedAsync(ServiceRequestStatus.Completato, r => r.CompletedAt = r.CreatedAt);

        var items = await TabAsync(s, SupplierInboxTab.AwaitingCustomer);

        Assert.Equal(new[] { sooner, later }, items.Select(item => item.Id));
        Assert.All(items, item => Assert.Equal(SupplierRequestPhases.ProposalPending, item.Phase));
        Assert.All(items, item => Assert.NotNull(item.Proposal));
    }

    [Fact]
    public async Task NewTab_StaysWhatItWas_EveryNewRequestWithOrWithoutAProposal_InTheSameOrder()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var toAnswerLate = await NewAsync(s, minutesToExpire: 90);
        var proposedSoon = await ProposedAsync(s, minutesToExpire: 10);
        var toAnswerSoon = await NewAsync(s, minutesToExpire: 30);
        var proposedNoDeadline = await ProposedAsync(s, minutesToExpire: null);
        await s.SeedAsync(ServiceRequestStatus.Annullato, r => r.CancelledAt = r.CreatedAt);

        var items = await TabAsync(s, SupplierInboxTab.New);

        // One list, one order (the deadline, the ones without last): not split, not reordered.
        Assert.Equal(new[] { proposedSoon, toAnswerSoon, toAnswerLate, proposedNoDeadline }, items.Select(item => item.Id));
        Assert.Equal(
            new[] { SupplierRequestPhases.ProposalPending, SupplierRequestPhases.ToAnswer, SupplierRequestPhases.ToAnswer, SupplierRequestPhases.ProposalPending },
            items.Select(item => item.Phase));
    }

    [Fact]
    public async Task ToAnswerAndAwaitingCustomer_NeverShareARequest_AndTogetherAreNuove()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        for (var i = 0; i < 4; i++)
            await NewAsync(s, minutesToExpire: 10 + i);
        for (var i = 0; i < 3; i++)
            await ProposedAsync(s, minutesToExpire: 20 + i);

        var toAnswer = (await TabAsync(s, SupplierInboxTab.ToAnswer)).Select(item => item.Id).ToList();
        var awaiting = (await TabAsync(s, SupplierInboxTab.AwaitingCustomer)).Select(item => item.Id).ToList();
        var all = (await TabAsync(s, SupplierInboxTab.New)).Select(item => item.Id).ToList();

        Assert.Equal(4, toAnswer.Count);
        Assert.Equal(3, awaiting.Count);
        Assert.Empty(toAnswer.Intersect(awaiting));
        Assert.Equal(all.Order(), toAnswer.Concat(awaiting).Order());
    }

    [Fact]
    public async Task TheTabs_TheTotalIsTheSizeOfTheTab_NotOfThePage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        for (var i = 0; i < 5; i++)
            await NewAsync(s, minutesToExpire: 10 + i);
        await ProposedAsync(s, minutesToExpire: 10);

        var query = QueryOf(SupplierInboxTab.ToAnswer) with { Page = 2, PageSize = 2 };
        var (items, total) = await s.Reader.ListAsync(s.SupplierOrgId, query);

        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task TheTabs_NeverHoldTheRequestsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var mine = await NewAsync(s, minutesToExpire: 30);
        await s.SeedAsync(ServiceRequestStatus.Richiesto, supplierOrgId: other);
        await s.SeedAsync(ServiceRequestStatus.Richiesto, ProposeOf, supplierOrgId: other);

        Assert.Equal(new[] { mine }, (await TabAsync(s, SupplierInboxTab.ToAnswer)).Select(item => item.Id));
        Assert.Empty(await TabAsync(s, SupplierInboxTab.AwaitingCustomer));
    }

    // ─── The phase ───

    [Fact]
    public async Task Phase_OfEveryRequest_IsDerivedFromItsStatusAndItsProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var expected = new Dictionary<Guid, string>
        {
            [await NewAsync(s, minutesToExpire: 30)] = "to_answer",
            [await ProposedAsync(s, minutesToExpire: 30)] = "proposal_pending",
            [(await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt)).Id] = "scheduled",
            [(await s.SeedAsync(ServiceRequestStatus.InCorso, r => r.TakenAt = r.CreatedAt)).Id] = "in_progress",
            [(await s.SeedAsync(ServiceRequestStatus.Completato, r => r.CompletedAt = r.CreatedAt)).Id] = "completed",
            [(await s.SeedAsync(ServiceRequestStatus.Pagato, r => r.PaidAt = r.CreatedAt)).Id] = "paid",
            [(await s.SeedAsync(ServiceRequestStatus.Rifiutato)).Id] = "rejected",
            [(await s.SeedAsync(ServiceRequestStatus.Annullato, r => r.CancelledAt = r.CreatedAt)).Id] = "cancelled",
        };

        var (items, total) = await s.Reader.ListAsync(s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 100));

        Assert.Equal(expected.Count, total);
        Assert.Equal(expected.OrderBy(pair => pair.Key), items.ToDictionary(item => item.Id, item => item.Phase).OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Phase_InTheDetail_IsTheSameAsInTheList()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var proposed = await ProposedAsync(s, minutesToExpire: 30);
        var completed = (await s.SeedAsync(ServiceRequestStatus.Completato, r => r.CompletedAt = r.CreatedAt)).Id;

        Assert.Equal("proposal_pending", (await s.Reader.GetAsync(proposed, s.SupplierOrgId))!.Phase);
        Assert.Equal("completed", (await s.Reader.GetAsync(completed, s.SupplierOrgId))!.Phase);
    }

    [Fact]
    public async Task Phase_OfARealFlow_FollowsTheRequestFromTheAnswerToThePayment()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        Assert.Equal("to_answer", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);

        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        Assert.Equal("proposal_pending", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        Assert.Equal("scheduled", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);

        await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        Assert.Equal("in_progress", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);

        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);
        Assert.Equal("completed", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);

        await s.Service.MarkPaidAsync(request.Id, s.HostOrgId);
        Assert.Equal("paid", (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Phase);
    }

    [Fact]
    public async Task Phase_AStatusTheCodeDoesNotKnow_IsUnknown_AndTheListStillWorks()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var id = (await s.SeedAsync((ServiceRequestStatus)42)).Id;

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 100));

        var item = Assert.Single(items);
        Assert.Equal(id, item.Id);
        Assert.Equal(SupplierRequestPhases.Unknown, item.Phase);
    }

    // ─── The source ───

    [Fact]
    public async Task Source_FiltersTheRequestsOfTheHostsFromTheOnesOfTheShowcase()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        var host = await NewAsync(s, minutesToExpire: 30);

        var all = await ListAsync(s, new SupplierInboxQuery([], null, null, 1, 100));
        var fromHosts = await ListAsync(s, new SupplierInboxQuery([], null, null, 1, 100, Source: ServiceRequestSource.Host));
        var fromTheShowcase = await ListAsync(s, new SupplierInboxQuery([], null, null, 1, 100, Source: ServiceRequestSource.Showcase));

        Assert.Equal(new[] { host, showcase.Id }.Order(), all.Select(item => item.Id).Order());
        Assert.Equal(new[] { host }, fromHosts.Select(item => item.Id));
        Assert.Equal(new[] { showcase.Id }, fromTheShowcase.Select(item => item.Id));
        Assert.Equal(SupplierRequestSources.CasaZen, fromHosts.Single().Source);
        Assert.Equal(SupplierRequestSources.Showcase, fromTheShowcase.Single().Source);
    }

    [Fact]
    public async Task Source_CombinesWithTheTabs()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        await NewAsync(s, minutesToExpire: 30);

        var query = QueryOf(SupplierInboxTab.ToAnswer) with { Source = ServiceRequestSource.Showcase };

        Assert.Equal(new[] { showcase.Id }, (await ListAsync(s, query)).Select(item => item.Id));
        Assert.Empty(await ListAsync(s, QueryOf(SupplierInboxTab.AwaitingCustomer) with { Source = ServiceRequestSource.Showcase }));
    }

    // ─── helpers ───

    private static SupplierInboxQuery QueryOf(SupplierInboxTab tab) => new(
        SupplierInboxTabs.StatusesOf(tab),
        null,
        null,
        1,
        100,
        SupplierInboxTabs.SortOf(tab),
        AwaitingCustomer: SupplierInboxTabs.AwaitingCustomerOf(tab));

    private static Task<IReadOnlyList<SupplierServiceRequestView>> TabAsync(ServiceRequestScenario s, SupplierInboxTab tab) =>
        ListAsync(s, QueryOf(tab));

    private static async Task<IReadOnlyList<SupplierServiceRequestView>> ListAsync(ServiceRequestScenario s, SupplierInboxQuery query) =>
        (await s.Reader.ListAsync(s.SupplierOrgId, query)).Items;

    /// <summary>A new request with no proposal, due <paramref name="minutesToExpire"/> minutes after the scenario clock (none when null).</summary>
    private static async Task<Guid> NewAsync(ServiceRequestScenario s, int? minutesToExpire) =>
        (await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = Due(minutesToExpire))).Id;

    /// <summary>A new request the supplier answered with another time (the customer has not answered).</summary>
    private static async Task<Guid> ProposedAsync(ServiceRequestScenario s, int? minutesToExpire) =>
        (await s.SeedAsync(
            ServiceRequestStatus.Richiesto,
            r =>
            {
                r.ResponseDueAt = Due(minutesToExpire);
                ProposeOf(r);
            })).Id;

    private static void ProposeOf(ServiceRequest request)
    {
        request.ProposedStartUtc = ServiceRequestScenario.FridayAt14;
        request.ProposedEndUtc = ServiceRequestScenario.FridayAt14.AddHours(2);
        request.ProposedAt = ServiceRequestScenario.Instant.UtcDateTime;
        request.ProposedByUserId = ServiceRequestScenario.SupplierUserId;
    }

    private static DateTime? Due(int? minutes) =>
        minutes is { } m ? ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(m) : null;
}
