using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: the service and the time of a request. A host picks a service of the supplier's catalog and a start; the end is the
/// service duration or an explicit duration. The slot is judged by the planner of SP-03 unless the host typed a duration. A
/// request without a start is rejected. Same-day jobs may both be taken when their intervals do not overlap. The lock that
/// keeps two requests off the same slot needs PostgreSQL and is in <c>ServiceRequestSchedulePostgresTests</c>.
/// </summary>
public class ServiceRequestScheduleTests
{
    // ─── Create: the service ───

    [Fact]
    public async Task CreateAsync_ServiceAndFreeSlot_KeepsTheTimeTheNameTheEstimateAndTheDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var created = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var saved = await s.ReadAsync(created.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ScheduledEndUtc);
        Assert.Equal(s.ListingId, saved.ServiceListingId);
        Assert.Equal(ServiceRequestScenario.ServiceName, saved.ServiceNameSnapshot);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, saved.EstimatedAmountCents);
        Assert.Null(saved.QuotedAmountCents);
        Assert.Null(saved.FinalAmountCents);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(120), saved.ResponseDueAt);
    }

    [Fact]
    public async Task CreateAsync_WithoutAStart_AssignsTenAmOnTheStayCheckOutDay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var created = await s.Service.CreateAsync(new CreateServiceRequestCommand(
            s.HostOrgId,
            ServiceRequestScenario.HostUserId,
            s.PropertyId,
            s.BookingId,
            s.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            null,
            false,
            ServiceListingId: s.ListingId,
            ScheduledStartUtc: null));

        // Check-out is 2026-10-12 UTC midnight → 10:00 Europe/Rome that day (08:00Z in CEST).
        var expectedStart = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedStart, created.ScheduledStartUtc);
        Assert.Equal(expectedStart.AddMinutes(ServiceRequestScenario.ServiceMinutes), created.ScheduledEndUtc);
    }

    [Fact]
    public async Task CreateAsync_NoServiceWithStartAndDuration_KeepsTheIntervalAndTheDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var created = await s.RequestAsync(ServiceRequestScenario.FridayAt10, withService: false);

        var saved = await s.ReadAsync(created.Id);
        Assert.Null(saved.ServiceListingId);
        Assert.Null(saved.ServiceNameSnapshot);
        Assert.Null(saved.EstimatedAmountCents);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ScheduledEndUtc);
        Assert.Equal("[]", saved.OptionsJson);
        Assert.Equal("[]", saved.PriceLinesJson);
        Assert.Equal("[]", saved.WorkPhotosJson);
        Assert.False(saved.FinalAmountNeedsConfirmation);
        Assert.NotNull(saved.ResponseDueAt);
    }

    [Fact]
    public async Task CreateAsync_LongRentWithServiceAndTime_UsesTheSameRules()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var created = await s.RequestAsync(ServiceRequestScenario.FridayAt10, rentalContext: ServiceRequestRentalContext.LongRent);

        var saved = await s.ReadAsync(created.Id);
        Assert.Equal(ServiceRequestRentalContext.LongRent, saved.RentalContext);
        Assert.Null(saved.BookingId);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
    }

    [Fact]
    public async Task CreateAsync_ResponseWindowFromTheOptions_SetsTheDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync(new ServiceRequestOptions { HostResponseMinutes = 45 });

        var created = await s.RequestAsync();

        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(45), (await s.ReadAsync(created.Id)).ResponseDueAt);
    }

    [Theory]
    [InlineData(SupplierServicePriceUnit.PerHour, false, 4000)] // the hours are not known yet
    [InlineData(SupplierServicePriceUnit.PerSet, false, 4000)]
    [InlineData(SupplierServicePriceUnit.PerSquareMeter, false, 4000)]
    [InlineData(SupplierServicePriceUnit.PerJob, true, 4000)] // on quote
    [InlineData(SupplierServicePriceUnit.PerJob, false, null)] // no price at all
    [InlineData(SupplierServicePriceUnit.PerJob, false, 0)]
    public async Task CreateAsync_ServiceWithoutAPriceKnownInAdvance_HasNoEstimate(
        SupplierServicePriceUnit unit, bool requiresQuote, int? priceFromCents)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var listingId = await s.AddListingAsync("Servizio a quantita", priceFromCents: priceFromCents, priceUnit: unit, requiresQuote: requiresQuote);

        var created = await s.RequestAsync(serviceListingId: listingId);

        Assert.Null((await s.ReadAsync(created.Id)).EstimatedAmountCents);
    }

    [Fact]
    public async Task CreateAsync_ServiceNotFound_Throws404AndCreatesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.RequestAsync(serviceListingId: Guid.NewGuid()));

        Assert.Equal(ServiceRequestErrorCodes.ServiceNotFound, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.ServiceNotFoundMessageKey, ex.MessageKey);
        Assert.Empty(s.Db.ServiceRequests);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CreateAsync_ServiceOfAnotherSupplier_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var otherSupplier = await s.AddOtherSupplierAsync();
        var foreignListing = await s.AddListingAsync("Del collega", supplierOrgId: otherSupplier);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.RequestAsync(serviceListingId: foreignListing));

        Assert.Equal(ServiceRequestErrorCodes.ServiceNotFound, ex.Code);
        Assert.Empty(s.Db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_DeletedService_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var deleted = await s.AddListingAsync("Tolto", deletedAt: ServiceRequestScenario.Instant.UtcDateTime.AddDays(-1));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.RequestAsync(serviceListingId: deleted));

        Assert.Equal(ServiceRequestErrorCodes.ServiceNotFound, ex.Code);
    }

    [Theory]
    [InlineData(SupplierServiceListingStatus.Draft)]
    [InlineData(SupplierServiceListingStatus.Paused)]
    public async Task CreateAsync_ServiceNotPublished_Throws422ServiceUnavailable(SupplierServiceListingStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var listing = await s.AddListingAsync("Non in vendita", status: status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.RequestAsync(serviceListingId: listing));

        Assert.Equal(ServiceRequestErrorCodes.ServiceUnavailable, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.ServiceUnavailableMessageKey, ex.MessageKey);
        Assert.Empty(s.Db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_ServiceOfAnotherCategory_Throws422ServiceCategoryMismatch()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var plumbing = await s.AddListingAsync("Idraulica", category: ServiceCategories.Plumbing);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.RequestAsync(serviceListingId: plumbing));

        Assert.Equal(ServiceRequestErrorCodes.ServiceCategoryMismatch, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.ServiceCategoryMismatchMessageKey, ex.MessageKey);
        Assert.Empty(s.Db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_TimeWithoutAServiceOrDuration_Throws422TimeNeedsService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.CreateAsync(new CreateServiceRequestCommand(
            s.HostOrgId,
            ServiceRequestScenario.HostUserId,
            s.PropertyId,
            s.BookingId,
            s.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            null,
            false,
            ScheduledStartUtc: ServiceRequestScenario.FridayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.TimeNeedsService, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.TimeNeedsServiceMessageKey, ex.MessageKey);
        Assert.Empty(s.Db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_TimeForAServiceWithoutDuration_Throws422TimeNeedsService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var noDuration = await s.AddListingAsync("Senza durata", durationMinutes: null);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, serviceListingId: noDuration, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.TimeNeedsService, ex.Code);
    }

    // ─── Create: the slot ───

    [Fact]
    public async Task CreateAsync_TimeOnADayTheSupplierDoesNotWork_Throws409SlotUnavailableAndSendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.SundayAt10, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailableMessageKey, ex.MessageKey);
        Assert.Empty(s.Db.ServiceRequests);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Theory]
    [InlineData("2026-10-09T18:00:00Z")] // 20:00 in Rome: after the evening band
    [InlineData("2026-10-09T05:00:00Z")] // 07:00 in Rome: before the morning band
    [InlineData("2026-10-09T10:00:00Z")] // 12:00 in Rome: the two hours would run into the lunch break
    [InlineData("2026-10-09T08:15:00Z")] // 10:15 in Rome: not one of the slots the supplier offers
    public async Task CreateAsync_TimeThePlannerDoesNotOffer_Throws409SlotUnavailable(string startUtc)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var start = DateTime.Parse(
            startUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.RequestAsync(start, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        Assert.Empty(s.Db.ServiceRequests);
    }

    [Theory]
    [InlineData("2026-10-09T06:00:00Z")] // 08:00 in Rome: the first slot of the morning
    [InlineData("2026-10-09T09:00:00Z")] // 11:00 in Rome: the last one, the work ends with the band at 13:00
    [InlineData("2026-10-09T12:00:00Z")] // 14:00 in Rome: the first of the afternoon
    public async Task CreateAsync_TimeInsideTheHours_IsAccepted(string startUtc)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var start = DateTime.Parse(
            startUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var created = await s.RequestAsync(start, usePublishedSlots: true);

        Assert.Equal(start, (await s.ReadAsync(created.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task CreateAsync_SlotAlreadyHeldByAnotherRequest_Throws409AndLeavesOnlyTheFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var first = await s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true);
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        Assert.Equal(first.Id, Assert.Single(await s.Db.ServiceRequests.AsNoTracking().ToListAsync()).Id);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CreateAsync_SlotInsideTheBufferAfterAnotherJob_IsRefusedAndTheOneAfterTheBufferIsNot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // 08:00-10:00 in Rome; 30 minutes of buffer: 10:00 is too close, 11:00 is the next slot of the supplier.
        await s.RequestAsync(new DateTime(2026, 10, 9, 6, 0, 0, DateTimeKind.Utc), usePublishedSlots: true);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true));
        var second = await s.RequestAsync(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc), usePublishedSlots: true);

        Assert.Equal(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc), (await s.ReadAsync(second.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task CreateAsync_ServiceWithItsOwnNotice_RefusesASlotInsideIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // Friday 10:00 is 22 hours from now (Thursday 12:00): too soon for a service that wants 48 hours.
        var withNotice = await s.AddListingAsync("Con preavviso", minNoticeHours: 48);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, serviceListingId: withNotice, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_ServiceNotOfferedThatWeekday_RefusesTheSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var mondayToThursday = await s.AddListingAsync(
            "Dal lunedi al giovedi",
            weekdaysMask: SupplierServiceWeekdays.ToMask([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday]));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, serviceListingId: mondayToThursday, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_SupplierOnTimeOff_RefusesTheSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.Agenda.AddTimeOffAsync(
            s.SupplierOrgId,
            new SupplierTimeOffInput(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9), SupplierTimeOffReason.Holiday, null));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true));
    }

    [Fact]
    public async Task CreateAsync_DayWithTheDailyMaximumReached_RefusesTheSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.Agenda.ReplaceRulesAsync(s.SupplierOrgId, new SupplierRulesInput(0, 1, 0, 35, 60));
        await s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt14, usePublishedSlots: true));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_SlotOfARequestThatWasCancelledOrRejected_IsFreeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var cancelled = await s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true);
        await s.Service.CancelAsHostAsync(cancelled.Id, s.HostOrgId, "Cambio di programma");
        var rejected = await s.RequestAsync(ServiceRequestScenario.FridayAt14, usePublishedSlots: true);
        await s.Service.RejectAsync(rejected.Id, s.SupplierOrgId, "Non posso");

        // Both slots are free again.
        var again10 = await s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true);
        var again14 = await s.RequestAsync(ServiceRequestScenario.FridayAt14, usePublishedSlots: true);

        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(again10.Id)).ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14, (await s.ReadAsync(again14.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task CreateAsync_CategoryOnlyJobsWithDuration_SkipThePlannerDailyMaximum()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // Duration-typed creates skip the published-slot check, so they can exceed the daily maximum of timed catalog slots.
        var origin = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 4; i++)
            await s.RequestAsync(origin.AddHours(i * 3), withService: false, durationMinutes: 60);

        var timed = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(timed.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task CreateAsync_TimeWithoutAKind_IsReadAsUtc()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var unspecified = DateTime.SpecifyKind(ServiceRequestScenario.FridayAt10, DateTimeKind.Unspecified);

        var created = await s.RequestAsync(unspecified);

        Assert.Equal(DateTimeKind.Utc, (await s.ReadAsync(created.Id)).ScheduledStartUtc!.Value.Kind);
        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(created.Id)).ScheduledStartUtc);
    }

    // ─── Take: time and price ───

    [Fact]
    public async Task TakeAsync_WithATime_SetsItFromTheDurationOfTheServiceAndClearsTheDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync();

        var taken = await s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId,
            new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ScheduledEndUtc);
        Assert.Null(saved.ResponseDueAt);
        Assert.NotNull(saved.TakenAt);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
    }

    [Fact]
    public async Task TakeAsync_WithATimeAndAnEnd_UsesTheEndOfTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync(withService: false);

        var taken = await s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId,
            new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10, ServiceRequestScenario.FridayAt10.AddMinutes(90)));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(90), saved.ScheduledEndUtc);
    }

    [Fact]
    public async Task TakeAsync_WithAQuote_KeepsItAndLeavesTheEstimateAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var taken = await s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(QuotedAmountCents: 7500));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(7500, saved.QuotedAmountCents);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, saved.EstimatedAmountCents);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SupplierServiceCatalogLimits.MaxAmountCents + 1)]
    public async Task TakeAsync_QuoteOutsideTheLimits_Throws422AmountInvalidAndLeavesTheRequestNew(int quote)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(QuotedAmountCents: quote)));

        Assert.Equal(ServiceRequestErrorCodes.AmountInvalid, ex.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task TakeAsync_TimeOutsideTheHours_Throws409SlotUnavailableAndLeavesTheRequestNew()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(ServiceRequestScenario.SundayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Null(saved.ScheduledStartUtc);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task TakeAsync_SlotHeldByAnotherRequest_Throws409SlotUnavailable()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.RequestAsync(ServiceRequestScenario.FridayAt10);
        var other = await s.SeedUntimedAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.TakeAsync(
            other.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
    }

    [Fact]
    public async Task TakeAsync_RequestThatAlreadyHasATime_KeepsItAndNeedsNoNewSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        // Sending the time the host chose again is not a change: the request is not "in its own way".
        var taken = await s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId,
            new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10));

        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(taken.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task TakeAsync_AnotherTimeThanTheOneTheHostChose_Throws422TimeAlreadySet()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt14)));

        Assert.Equal(ServiceRequestErrorCodes.TimeAlreadySet, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.TimeAlreadySetMessageKey, ex.MessageKey);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task TakeAsync_WithoutATimeOfTheHost_KeepsTheTimeTheHostChose()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var taken = await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.ScheduledEndUtc);
    }

    [Fact]
    public async Task TakeAsync_TimeForARequestWithoutServiceAndWithoutAnEnd_Throws422TimeInvalid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync(withService: false);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.TimeInvalidMessageKey, ex.MessageKey);
    }

    [Theory]
    [InlineData(0)] // the end is the start
    [InlineData(-30)] // the end is before the start
    [InlineData(SupplierServiceCatalogLimits.MaxDurationMinutes + 1)] // longer than a service can last
    public async Task TakeAsync_EndThatIsNotAfterTheStartOrIsTooLong_Throws422TimeInvalid(int minutesAfterStart)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId,
            new TakeServiceRequestCommand(ServiceRequestScenario.FridayAt10, ServiceRequestScenario.FridayAt10.AddMinutes(minutesAfterStart))));

        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, ex.Code);
    }

    [Fact]
    public async Task TakeAsync_EndWithoutAStart_Throws422TimeInvalid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedUntimedAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId,
            new TakeServiceRequestCommand(ScheduledEndUtc: ServiceRequestScenario.FridayAt10)));

        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, ex.Code);
    }

    [Fact]
    public async Task TakeAsync_TakenRequestKeepsItsSlot_SoTheNextRequestCannotUseIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.TakenAsync(ServiceRequestScenario.FridayAt10);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => s.RequestAsync(ServiceRequestScenario.FridayAt10, usePublishedSlots: true));
    }

    [Fact]
    public async Task TakeAsync_TwoJobsTheSameDayWhoseTimesDoNotOverlap_BothAreTaken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.TakenAsync(ServiceRequestScenario.FridayAt10);

        var second = await s.TakenAsync(ServiceRequestScenario.FridayAt14);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, second.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, second.ScheduledStartUtc);
    }

    [Fact]
    public async Task TakeAsync_TwoJobsWhoseIntervalsOverlap_TheSecondConflicts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // Both stay Richiesto until the first take: create only conflicts with jobs already accepted.
        var first = await s.RequestAsync(ServiceRequestScenario.FridayAt10);
        var second = await s.RequestAsync(ServiceRequestScenario.FridayAt10, withService: false, durationMinutes: 60);
        await s.Service.TakeAsync(first.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            s.Service.TakeAsync(second.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));

        Assert.Equal(ServiceRequestErrorCodes.CommitmentConflict, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CommitmentConflictMessageKey, ex.MessageKey);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task CreateAsync_WithADurationAndNoCatalogService_KeepsTheIntervalTheHostAsked()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var created = await s.Service.CreateAsync(new CreateServiceRequestCommand(
            s.HostOrgId,
            ServiceRequestScenario.HostUserId,
            s.PropertyId,
            s.BookingId,
            s.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            null,
            false,
            ScheduledStartUtc: ServiceRequestScenario.FridayAt10,
            DurationMinutes: 90));

        Assert.Equal(ServiceRequestScenario.FridayAt10, created.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(90), created.ScheduledEndUtc);
        Assert.Null(created.ServiceListingId);
    }
}
