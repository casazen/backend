using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E24/F01/US01/T02 (NF-120, NF-21, NF-45): the service requests are on the event bus (<c>ServiceRequestCreated</c> and <c>ServiceRequestCompleted</c>, in the save of the change, ids
/// only), a request can be made at a price the host agreed beforehand, and the host can move a request nobody answered to another time. On the world of
/// <see cref="ServiceRequestScenario"/> and the real <see cref="ServiceRequestService"/>.
/// </summary>
public class ServiceRequestEventsAndRescheduleTests
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task<List<OutboxEvent>> EventsAsync(ServiceRequestScenario scenario) =>
        await scenario.Db.OutboxEvents.AsNoTracking().OrderBy(e => e.EventType).ThenBy(e => e.Id).ToListAsync();

    // ─── The events ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The creation of a host's request is on the bus once: the org of the host, the request, a key per request and a payload of ids and the category, nothing of the host's notes or the price.</summary>
    [Fact]
    public async Task Create_RecordsServiceRequestCreated_OnceWithIdsOnly()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync(recordEvents: true);

        var request = await scenario.RequestAsync();

        var recorded = Assert.Single(await EventsAsync(scenario));
        Assert.Equal((DomainEventType.ServiceRequestCreated, scenario.HostOrgId, request.Id), (recorded.EventType, recorded.OrgId, recorded.EntityId));
        Assert.Equal($"service-request-created:{request.Id:N}", recorded.DedupKey);
        var payload = JsonSerializer.Deserialize<ServiceRequestEventPayload>(recorded.PayloadJson, ReadOptions)!;
        Assert.Equal(
            (request.Id, scenario.BookingId, scenario.PropertyId, scenario.SupplierOrgId, ServiceCategories.Cleaning, ServiceRequestRentalContext.ShortRent, ServiceRequestStatus.Richiesto),
            (payload.ServiceRequestId, payload.BookingId, payload.PropertyId, payload.SupplierOrgId, payload.Category, payload.RentalContext, payload.Status));
        // Ids and codes only (ADR-022-R21): the notes of the host, the address and the price are not in it.
        Assert.DoesNotContain(ServiceRequestScenario.HostNotes, recorded.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, recorded.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceRequestScenario.ServicePriceCents.ToString(System.Globalization.CultureInfo.InvariantCulture), recorded.PayloadJson, StringComparison.Ordinal);
        // The routines read the stay by the name they always read it by, whatever the case.
        using var document = JsonDocument.Parse(recorded.PayloadJson);
        Assert.Contains(document.RootElement.EnumerateObject(), m => string.Equals(m.Name, "bookingId", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The completion is on the bus once for the request, in the save of the transition, with the status it has after it; nothing at all is recorded without a recorder.</summary>
    [Fact]
    public async Task Complete_RecordsServiceRequestCompleted_OnceForTheRequest()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync(recordEvents: true);
        var taken = await scenario.TakenAsync();

        var completed = await scenario.Service.CompleteAsync(taken.Id, scenario.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 6500));

        var events = await EventsAsync(scenario);
        Assert.Equal([DomainEventType.ServiceRequestCreated, DomainEventType.ServiceRequestCompleted], events.Select(e => e.EventType));
        var done = events[1];
        Assert.Equal((completed.Id, scenario.HostOrgId, $"service-request-completed:{completed.Id:N}"), (done.EntityId, done.OrgId, done.DedupKey));
        var payload = JsonSerializer.Deserialize<ServiceRequestEventPayload>(done.PayloadJson, ReadOptions)!;
        Assert.Equal((ServiceRequestStatus.Completato, scenario.BookingId), (payload.Status, payload.BookingId));
        Assert.DoesNotContain("6500", done.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutARecorder_NothingIsRecorded()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var taken = await scenario.TakenAsync();

        await scenario.Service.CompleteAsync(taken.Id, scenario.SupplierOrgId);

        Assert.Empty(await EventsAsync(scenario));
    }

    // ─── The price agreed beforehand ──────────────────────────────────────────────────────────────────────────────────────────────

    private static CreateServiceRequestCommand Command(ServiceRequestScenario scenario, int? agreed) =>
        new(
            scenario.HostOrgId,
            ServiceRequestScenario.HostUserId,
            scenario.PropertyId,
            scenario.BookingId,
            scenario.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            "Note",
            ChargeToGuest: false,
            ServiceRequestRentalContext.ShortRent,
            ServiceListingId: null,
            ScheduledStartUtc: ServiceRequestScenario.FridayAt10,
            DurationMinutes: 120,
            AgreedAmountCents: agreed);

    /// <summary>The price the host agreed is the estimated amount of the request (in place of the one of a catalog service); none leaves it empty as before.</summary>
    [Fact]
    public async Task AgreedAmount_IsTheEstimatedAmountOfTheRequest()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();

        var agreed = await scenario.Service.CreateAsync(Command(scenario, 5000));
        var none = await scenario.Service.CreateAsync(Command(scenario, null) with { ScheduledStartUtc = ServiceRequestScenario.SaturdayAt09 });

        Assert.Equal(5000, agreed.EstimatedAmountCents);
        Assert.Null(none.EstimatedAmountCents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1)]
    public async Task AgreedAmount_OutsideTheBoundsOfARequest_IsRefusedBeforeAnythingIsSaved(int agreed)
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => scenario.Service.CreateAsync(Command(scenario, agreed)));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, error.Code);
        Assert.Empty(scenario.Db.ServiceRequests);
    }

    // ─── Moving a request nobody answered ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The host moves a new request to another time: the interval changes, the request stays new, and nothing else of it.</summary>
    [Fact]
    public async Task Reschedule_MovesANewRequestToAnotherTime()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var request = await scenario.RequestAsync();
        var start = ServiceRequestScenario.SaturdayAt09;

        var moved = await scenario.Service.RescheduleAsHostAsync(request.Id, scenario.HostOrgId, start, start.AddMinutes(90));

        Assert.Equal((start, start.AddMinutes(90), ServiceRequestStatus.Richiesto), (moved.ScheduledStartUtc, moved.ScheduledEndUtc, moved.Status));
        var stored = await scenario.ReadAsync(request.Id);
        Assert.Equal((start, start.AddMinutes(90), request.ServiceListingId, request.EstimatedAmountCents), (stored.ScheduledStartUtc, stored.ScheduledEndUtc, stored.ServiceListingId, stored.EstimatedAmountCents));
    }

    /// <summary>What the supplier took, proposed, offered or shares with a backup is not moved by the host: 422, and the host decides with the actions it has.</summary>
    [Theory]
    [InlineData("taken")]
    [InlineData("proposed")]
    [InlineData("quote")]
    [InlineData("group")]
    [InlineData("completed")]
    public async Task Reschedule_RefusesARequestThatIsNotSimplyWaiting(string state)
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var request = await scenario.RequestAsync();
        await scenario.ChangeRequestAsync(request.Id, r =>
        {
            switch (state)
            {
                case "taken":
                    r.Status = ServiceRequestStatus.PresoInCarico;
                    break;
                case "completed":
                    r.Status = ServiceRequestStatus.Completato;
                    break;
                case "proposed":
                    r.ProposedStartUtc = ServiceRequestScenario.FridayAt14;
                    r.ProposedEndUtc = ServiceRequestScenario.FridayAt14.AddHours(2);
                    r.ProposedAt = ServiceRequestScenario.Instant.UtcDateTime;
                    break;
                case "quote":
                    r.QuoteSentAt = ServiceRequestScenario.Instant.UtcDateTime;
                    r.QuoteLinesJson = """[{"label":"Pulizia","amountCents":8000}]""";
                    r.QuoteTotalCents = 8000;
                    break;
                default:
                    r.GroupId = Guid.NewGuid();
                    r.GroupRole = ServiceRequestGroupRole.Primary;
                    break;
            }
        });
        var before = await scenario.ReadAsync(request.Id);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => scenario.Service.RescheduleAsHostAsync(
            request.Id, scenario.HostOrgId, ServiceRequestScenario.SaturdayAt09, ServiceRequestScenario.SaturdayAt09.AddHours(2)));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, error.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotRescheduleMessageKey, error.MessageKey);
        Assert.Equal(before.ScheduledStartUtc, (await scenario.ReadAsync(request.Id)).ScheduledStartUtc);
    }

    /// <summary>Another org's request is not found (the same 404 as a missing one), and so is a request of a supplier's showcase.</summary>
    [Fact]
    public async Task Reschedule_OfAnotherOrgsRequest_IsNotFound()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var request = await scenario.RequestAsync();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => scenario.Service.RescheduleAsHostAsync(
            request.Id, scenario.SupplierOrgId, ServiceRequestScenario.SaturdayAt09, ServiceRequestScenario.SaturdayAt09.AddHours(2)));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, error.Code);
        Assert.Equal(request.ScheduledStartUtc, (await scenario.ReadAsync(request.Id)).ScheduledStartUtc);
    }

    /// <summary>The new time is checked as a take is: it must not overlap a job the supplier already took (409), and it must be an interval of whole minutes within the bounds (422).</summary>
    [Fact]
    public async Task Reschedule_ChecksTheNewTime()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var request = await scenario.RequestAsync();
        await scenario.SeedAsync(ServiceRequestStatus.PresoInCarico, r =>
        {
            r.BookingId = null;
            r.ScheduledStartUtc = ServiceRequestScenario.SaturdayAt09.AddMinutes(30);
            r.ScheduledEndUtc = ServiceRequestScenario.SaturdayAt09.AddHours(2);
        });
        var start = ServiceRequestScenario.SaturdayAt09;

        var busy = await Assert.ThrowsAsync<DomainConflictException>(
            () => scenario.Service.RescheduleAsHostAsync(request.Id, scenario.HostOrgId, start, start.AddHours(2)));
        var backwards = await Assert.ThrowsAsync<DomainRuleException>(
            () => scenario.Service.RescheduleAsHostAsync(request.Id, scenario.HostOrgId, start, start.AddHours(-1)));
        var seconds = await Assert.ThrowsAsync<DomainRuleException>(
            () => scenario.Service.RescheduleAsHostAsync(request.Id, scenario.HostOrgId, start, start.AddSeconds(90)));

        Assert.Equal(ServiceRequestErrorCodes.CommitmentConflict, busy.Code);
        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, backwards.Code);
        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, seconds.Code);
        Assert.Equal(request.ScheduledStartUtc, (await scenario.ReadAsync(request.Id)).ScheduledStartUtc);
    }
}
