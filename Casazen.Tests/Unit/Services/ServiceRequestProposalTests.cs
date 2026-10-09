using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: the supplier proposes another time for a new request, the host accepts or turns it down, and the supplier accepts many
/// requests at once. The semantics chosen for the proposal: it is an offer, not a reservation (it holds no slot); the host's
/// acceptance takes the request on the supplier's behalf at that time, after checking the slot again under the supplier's
/// calendar lock; turning it down leaves the request new, still waiting for the supplier.
/// </summary>
public class ServiceRequestProposalTests
{
    // ─── Propose ───

    [Fact]
    public async Task ProposeTimeAsync_NewRequest_StoresTheProposalAndTellsTheHostWithoutChangingTheRequest()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var deadline = (await s.ReadAsync(request.Id)).ResponseDueAt;
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(10));

        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, Message: "  Posso solo nel pomeriggio  "));

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, saved.ProposedStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ProposedEndUtc);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.ProposedAt);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.ProposedByUserId);
        Assert.Equal("Posso solo nel pomeriggio", saved.ProposalMessage);
        // The request itself is untouched: no time, no taker, the same deadline.
        Assert.Null(saved.ScheduledStartUtc);
        Assert.Null(saved.TakenAt);
        Assert.Equal(deadline, saved.ResponseDueAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("host@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestTimeProposed, email.Template);
        Assert.Contains("Posso solo nel pomeriggio", email.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.PropertyHosts(s.PropertyId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestTimeProposed, push.Payload.Type);
        Assert.Equal(PushDeliveryKeys.ServiceRequestTimeProposed(request.Id, saved.ProposedAt!.Value), push.DeliveryKey);
    }

    [Fact]
    public async Task ProposeTimeAsync_WithAnExplicitEnd_UsesItAndDoesNotNeedAService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(withService: false);

        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt10, ServiceRequestScenario.FridayAt10.AddMinutes(90)));

        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(90), (await s.ReadAsync(request.Id)).ProposedEndUtc);
    }

    [Fact]
    public async Task ProposeTimeAsync_NoServiceAndNoEnd_Throws422TimeInvalid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(withService: false);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, ex.Code);
        Assert.Null((await s.ReadAsync(request.Id)).ProposedStartUtc);
    }

    [Fact]
    public async Task ProposeTimeAsync_TimeTheSupplierCannotDo_Throws409SlotUnavailableAndTellsNobody()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.SundayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        Assert.Null((await s.ReadAsync(request.Id)).ProposedStartUtc);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task ProposeTimeAsync_RequestThatHasATime_MayProposeAnotherAndItsOwnSlotIsNotInTheWay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10); // 10:00-12:00

        // 11:00-13:00 overlaps the request's own slot: only the request itself is there, so it is free.
        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc)));

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc), saved.ProposedStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
    }

    [Fact]
    public async Task ProposeTimeAsync_ASecondProposal_ReplacesTheFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var command = new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, Message: "Primo");
        await s.Service.ProposeTimeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, command);

        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.SaturdayAt09, Message: null));

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09, saved.ProposedStartUtc);
        Assert.Null(saved.ProposalMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProposeTimeAsync_BlankMessage_IsStoredAsNoMessage(string message)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, Message: message));

        Assert.Null((await s.ReadAsync(request.Id)).ProposalMessage);
    }

    [Fact]
    public async Task ProposeTimeAsync_ProposalHoldsNoSlot_SoAnotherRequestCanTakeTheSameTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        var other = await s.RequestAsync(ServiceRequestScenario.FridayAt14);

        Assert.Equal(ServiceRequestScenario.FridayAt14, (await s.ReadAsync(other.Id)).ScheduledStartUtc);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task ProposeTimeAsync_RequestNotNew_Throws422WithTheProposeMessage(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14)));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotProposeMessageKey, ex.MessageKey);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task ProposeTimeAsync_RequestOfAnotherSupplier_IsForbidden()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var other = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.ProposeTimeAsync(
            request.Id, other, "auth0|other", new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14)));
    }

    [Fact]
    public async Task ProposeTimeAsync_SuspendedSupplier_Throws422SupplierNotActive()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14)));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
    }

    [Fact]
    public async Task TakeAsync_AfterAProposal_DropsTheProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedAt);
    }

    [Fact]
    public async Task RejectAsync_AfterAProposal_DropsTheProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non posso piu");

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposalMessage);
        Assert.Null(saved.ResponseDueAt);
    }

    // ─── Accept the proposal ───

    [Fact]
    public async Task AcceptProposalAsync_Proposal_TakesTheRequestOnTheSuppliersBehalfAtTheProposedTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        var proposedAt = (await s.ReadAsync(request.Id)).ProposedAt!.Value;
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var accepted = await s.Service.AcceptProposalAsync(request.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, accepted.Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ScheduledEndUtc);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.TakenAt);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedEndUtc);
        Assert.Null(saved.ProposedAt);
        Assert.Null(saved.ProposedByUserId);
        Assert.Null(saved.ProposalMessage);

        // Only the supplier is told: the host is the one who accepted.
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestProposalAnswered, email.Template);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestProposalAccepted, push.Payload.Type);
        Assert.Equal(PushDeliveryKeys.ServiceRequestProposalAnswered(request.Id, proposedAt, accepted: true), push.DeliveryKey);
    }

    [Fact]
    public async Task AcceptProposalAsync_AcceptedRequestHoldsTheSlotAndGoesOnLikeATakenOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.AcceptProposalAsync(request.Id, s.HostOrgId);

        await Assert.ThrowsAsync<DomainConflictException>(() => s.RequestAsync(ServiceRequestScenario.FridayAt14));
        var started = await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        Assert.Equal(ServiceRequestStatus.InCorso, started.Status);
    }

    [Fact]
    public async Task AcceptProposalAsync_NoProposal_Throws422NoProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptProposalAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.NoProposal, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.NoProposalMessageKey, ex.MessageKey);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task AcceptProposalAsync_RequestAlreadyTaken_Throws422NoProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptProposalAsync(taken.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.NoProposal, ex.Code);
    }

    [Fact]
    public async Task AcceptProposalAsync_SlotTakenMeanwhile_Throws409AndKeepsTheProposalForTheSupplierToChange()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.RequestAsync(ServiceRequestScenario.FridayAt14); // another request gets the slot first
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AcceptProposalAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, saved.ProposedStartUtc);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task AcceptProposalAsync_SupplierSuspendedMeanwhile_Throws422SupplierInactive()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptProposalAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.SupplierInactive, ex.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task AcceptProposalAsync_RequestOfAnotherHost_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.AcceptProposalAsync(request.Id, Guid.NewGuid()));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── Turn the proposal down ───

    [Fact]
    public async Task RejectProposalAsync_Proposal_DropsItKeepsTheRequestNewAndTellsTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var deadline = (await s.ReadAsync(request.Id)).ResponseDueAt;
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        s.ForgetNotifications();

        var rejected = await s.Service.RejectProposalAsync(request.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.Richiesto, rejected.Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedEndUtc);
        Assert.Null(saved.ProposedAt);
        Assert.Null(saved.ProposedByUserId);
        Assert.Null(saved.ProposalMessage);
        Assert.Equal(deadline, saved.ResponseDueAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestProposalAnswered, email.Template);
        Assert.Equal(PushTypes.ServiceRequestProposalRejected, Assert.Single(s.Pushes).Payload.Type);
    }

    [Fact]
    public async Task RejectProposalAsync_NoProposal_Throws422NoProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RejectProposalAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.NoProposal, ex.Code);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RejectProposalAsync_AnsweredOnce_CannotBeAnsweredAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.RejectProposalAsync(request.Id, s.HostOrgId);

        await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RejectProposalAsync(request.Id, s.HostOrgId));
        await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptProposalAsync(request.Id, s.HostOrgId));
    }

    [Fact]
    public async Task RejectProposalAsync_ThenTheSupplierCanProposeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.RejectProposalAsync(request.Id, s.HostOrgId);

        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.SaturdayAt09));

        Assert.Equal(ServiceRequestScenario.SaturdayAt09, (await s.ReadAsync(request.Id)).ProposedStartUtc);
    }

    [Fact]
    public async Task RejectProposalAsync_RequestOfAnotherHost_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RejectProposalAsync(request.Id, Guid.NewGuid()));
    }

    // ─── Accept many ───

    [Fact]
    public async Task AcceptManyAsync_NewRequests_TakesThemAllAndAnswersEachRowInOrder()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var requests = new List<ServiceRequest> { await s.RequestAsync(), await s.RequestAsync(), await s.RequestAsync() };
        s.ForgetNotifications();

        var results = await s.Service.AcceptManyAsync(
            s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, requests.Select(r => r.Id).ToList());

        Assert.Equal(requests.Select(r => r.Id), results.Select(r => r.Id));
        Assert.All(results, result =>
        {
            Assert.True(result.Accepted);
            Assert.Equal(ServiceRequestStatus.PresoInCarico, result.Status);
            Assert.Null(result.Code);
            Assert.Null(result.MessageKey);
        });
        foreach (var request in requests)
            Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(request.Id)).Status);

        // Each taken request tells the host as a single take does.
        Assert.Equal(3, s.Emails.Snapshot().Count(email => email.To == "host@test.com"));
        Assert.Equal(3, s.Pushes.Count(push => push.Payload.Type == PushTypes.ServiceRequestTaken));
    }

    [Fact]
    public async Task AcceptManyAsync_SomeRowsFail_EachFailureIsReportedAndTheOthersAreStillTaken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var fresh = await s.RequestAsync();
        var alreadyTaken = await s.TakenAsync();
        var otherSupplier = await s.AddOtherSupplierAsync();
        var foreign = await s.SeedAsync(ServiceRequestStatus.Richiesto, supplierOrgId: otherSupplier);
        var unknown = Guid.NewGuid();
        var rejected = await s.SeedAsync(ServiceRequestStatus.Rifiutato);
        var last = await s.RequestAsync();

        var results = await s.Service.AcceptManyAsync(
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            [fresh.Id, alreadyTaken.Id, foreign.Id, unknown, rejected.Id, last.Id]);

        Assert.Equal(6, results.Count);
        Assert.True(results[0].Accepted);
        Assert.False(results[1].Accepted);
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, results[1].Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotTakeMessageKey, results[1].MessageKey);
        // A request of another supplier answers like a missing one: the batch never tells that it exists.
        Assert.Equal(ServiceRequestErrorCodes.NotFound, results[2].Code);
        Assert.Equal(ServiceRequestErrorCodes.NotFoundMessageKey, results[2].MessageKey);
        Assert.Equal(ServiceRequestErrorCodes.NotFound, results[3].Code);
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, results[4].Code);
        Assert.True(results[5].Accepted);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(fresh.Id)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(last.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(foreign.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Rifiutato, (await s.ReadAsync(rejected.Id)).Status);
    }

    [Fact]
    public async Task AcceptManyAsync_TheSameIdTwice_IsTakenOnceAndAnsweredOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.ForgetNotifications();

        var results = await s.Service.AcceptManyAsync(
            s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [request.Id, request.Id]);

        var result = Assert.Single(results);
        Assert.True(result.Accepted);
        Assert.Single(s.Emails.Snapshot());
    }

    [Fact]
    public async Task AcceptManyAsync_MoreThanTheLimit_IsRefusedBeforeTakingAnything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var ids = new List<Guid> { request.Id };
        ids.AddRange(Enumerable.Range(0, ServiceRequestLimits.MaxBatchAccept).Select(_ => Guid.NewGuid()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, ids));

        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task AcceptManyAsync_ExactlyTheLimit_IsAccepted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < ServiceRequestLimits.MaxBatchAccept; i++)
            ids.Add((await s.RequestAsync(withService: false)).Id);

        var results = await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, ids);

        Assert.Equal(ServiceRequestLimits.MaxBatchAccept, results.Count);
        Assert.All(results, result => Assert.True(result.Accepted));
    }

    [Fact]
    public async Task AcceptManyAsync_NoIds_AnswersNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        Assert.Empty(await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, []));
    }

    [Fact]
    public async Task AcceptManyAsync_SuspendedSupplier_EveryRowFailsAndNothingIsTaken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var first = await s.RequestAsync();
        var second = await s.RequestAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var results = await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [first.Id, second.Id]);

        Assert.All(results, result =>
        {
            Assert.False(result.Accepted);
            Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, result.Code);
        });
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(first.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task AcceptManyAsync_RequestWithATimeOfTheHost_KeepsThatTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var results = await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [request.Id]);

        Assert.True(Assert.Single(results).Accepted);
        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
    }
}
