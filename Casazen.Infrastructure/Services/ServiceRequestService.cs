using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Service requests between a host org and a supplier org. Who may call each operation is decided by the web layer
/// (policies and the host resource handler, TN-3); this service only enforces the org boundaries it is given
/// (<see cref="HostScope"/>, host org id, supplier org id), the state machine and the rules of the schedule and the price.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> Every change of a request is saved only if nobody changed the request since it was read
/// (<c>xmin</c>, A4-19): the loser of two concurrent changes gets 409 <c>service_request_state_changed</c>. A change that
/// sets the <b>time</b> of a request (a host's request with a slot, the supplier's take or proposal, the host's acceptance of
/// the proposal) also runs under the supplier's calendar lock (<c>SupplierCalendarSync</c>, the lock of the agenda and of
/// the iCal sync) and checks the slot with the planner <b>after</b> taking it, so two requests never get the same slot
/// (409 <c>supplier_slot_unavailable</c>). Outside PostgreSQL nothing is locked.</para>
/// <para><b>Notifications</b> (<see cref="ServiceRequestNotifier"/>) are queued after the change is saved, by the winner only.
/// The operations of this class are split in files by topic: the lifecycle after the take (start, cancel, remind, propose, batch),
/// and the photos of the work.</para>
/// <para><b>Requests from the suppliers' public showcases (SP-10)</b> belong to the supplier and have a customer, not a host: the
/// operations of the supplier work on them as on any request (take, refuse, start, complete, cancel, propose another time) and
/// the customer is the one told; every operation of the <b>host</b> (list, read, cancel, remind, answer a proposal, mark as paid)
/// answers 404 for them, whatever the org of the caller: they are filtered by their context and by their org, two reasons that
/// do not depend on each other (the supplier org may also be a host org).</para>
/// </remarks>
public partial class ServiceRequestService(
    AppDbContext db,
    IServiceRequestRepository repository,
    ServiceRequestNotifier notifier,
    ISupplierComuneMatcher comuneMatcher,
    ILegalDocumentService legalDocuments,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    IFileStorage fileStorage,
    IImageStorageService images,
    IOptions<ServiceRequestOptions> options,
    IOptions<ShowcaseBookingOptions> showcaseOptions,
    ISupplierPaymentService payments,
    ILogger<ServiceRequestService> logger,
    TimeProvider? timeProvider = null,
    ISupplierFundsService? funds = null,
    IFeatureFlags? featureFlags = null,
    ServiceRequestBackupSelector? backupSelector = null,
    ISupplierReliabilityService? reliability = null,
    IDomainEventRecorder? recorder = null,
    IOptions<SupplierPaymentsOptions>? supplierPaymentOptions = null) : IServiceRequestService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly TimeOnly DefaultStartTime = new(10, 0);

    private sealed record RequestSchedule(DateTime Start, DateTime End, SupplierSlotQuery Query);

    public async Task<ServiceRequest> CreateAsync(
        CreateServiceRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command), command.RentalContext, "A showcase request is not created through the host's path.");
        }

        var category = ServiceCategories.Require(command.Category);

        if (command.AgreedAmountCents is { } agreedAmount)
            ServiceRequestPricing.EnsureValidAmount(agreedAmount);

        // IgnoreQueryFilters (tenant only): scoped by the explicit OrgId check below. command.OrgId comes from
        // OrgContextResolver, which may provision the org after the tenant filter cached a null org. The soft-delete
        // filter stays (PC-05): no new request on a deleted property.
        var property = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == command.PropertyId, cancellationToken);

        if (property is null || property.OrgId != command.OrgId)
        {
            throw new NotFoundException($"Property {command.PropertyId} not found for the service request")
            {
                Code = ServiceRequestErrorCodes.PropertyNotFound,
                MessageKey = ServiceRequestErrorCodes.PropertyNotFoundMessageKey,
            };
        }

        await EnsureBookingRuleAsync(command, cancellationToken);
        await EnsureLeaseRuleAsync(command, cancellationToken);

        if (command.ChargeToGuest)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ChargeToGuestNotAllowed, ServiceRequestErrorCodes.ChargeToGuestNotAllowedMessageKey);
        }

        var supplier = await SupplierServiceCatalogService.SuppliersForProperty(db, command.OrgId, command.PropertyId)
            .Include(sp => sp.Org)
            .FirstOrDefaultAsync(sp => sp.OrgId == command.SupplierOrgId, cancellationToken)
            ?? throw new NotFoundException($"Supplier {command.SupplierOrgId} not found")
            {
                Code = ServiceRequestErrorCodes.SupplierNotFound,
                MessageKey = ServiceRequestErrorCodes.SupplierNotFoundMessageKey,
            };

        if (supplier.Status != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        // By ISTAT code when the property has a chosen comune (SU-04), by the written city otherwise.
        if (!await comuneMatcher.CoversAsync(supplier, ComuneTarget.ForProperty(property), cancellationToken))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierOutsideComune, ServiceRequestErrorCodes.SupplierOutsideComuneMessageKey);
        }

        var service = await FindRequestableServiceAsync(command, category, cancellationToken);
        command = await EnsureTimedCreateCommandAsync(command, service, cancellationToken);
        var schedule = ResolveCreateSchedule(command, service);

        var backupSuppliers = await SelectBackupsAsync(command, property, category, schedule, cancellationToken);

        var now = Now();
        var request = new ServiceRequest
        {
            OrgId = command.OrgId,
            BookingId = command.BookingId,
            RentalContext = command.RentalContext,
            PropertyId = command.PropertyId,
            LeaseContractId = command.LeaseContractId,
            SupplierOrgId = command.SupplierOrgId,
            Category = category,
            Urgency = command.Urgency,
            Notes = command.Notes?.Trim() ?? string.Empty,
            ChargeToGuest = command.ChargeToGuest,
            Status = ServiceRequestStatus.Richiesto,
            CreatedAt = now,
            UpdatedAt = now,
            ServiceListingId = service?.Id,
            ServiceNameSnapshot = service?.Name,
            EstimatedAmountCents = command.AgreedAmountCents ?? service?.EstimatedAmountCents,
            ScheduledStartUtc = schedule.Start,
            ScheduledEndUtc = schedule.End,
            ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes),
        };

        var comune = property.City;
        var supplierEmail = notifier.RenderCreatedEmail(request, supplier.LegalName, comune);

        IReadOnlyList<(ServiceRequest Request, SupplierProfile Supplier)> backups = [];
        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(command.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            if (command.DurationMinutes is null)
                await EnsureSlotFreeAsync(command.SupplierOrgId, schedule, exceptRequestId: null, cancellationToken);

            await EnsureNoOverlappingCommitmentAsync(
                command.SupplierOrgId, request.Id, schedule.Start, schedule.End, cancellationToken);

            await AcceptAutomaticallyIfRegularAsync(request, command, supplier, service, cancellationToken);

            if (!request.AutoAccepted && backupSuppliers.Count > 0)
                backups = AttachBackupGroup(request, backupSuppliers, slotPlanned: command.DurationMinutes is null, now);

            if (supplierPaymentOptions?.Value.AddMarketplaceFeeToNewHostRequests == true)
            {
                var requests = backups.Select(b => b.Request).Prepend(request).ToArray();
                var supplierIds = requests.Select(r => r.SupplierOrgId).ToArray();
                var privateIds = await SupplierServiceCatalogService.HostOriginSupplierIds(db, request.OrgId)
                    .Where(id => supplierIds.Contains(id)).ToHashSetAsync(cancellationToken);
                var marketplaceRate = supplierPaymentOptions.Value.RequireCommissionPercent();
                foreach (var item in requests)
                    item.HostSurchargePercent = privateIds.Contains(item.SupplierOrgId) ? 0m : marketplaceRate;
            }

            RecordEvent(DomainEventType.ServiceRequestCreated, request, request.Status, ServiceRequestEventPayload.CreatedKeyPrefix);
            await repository.AddAsync(request, cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        if (!request.AutoAccepted)
            await notifier.QueueCreatedAsync(request, supplier.Email, supplierEmail, comune, cancellationToken);

        if (backups.Count > 0)
            await notifier.NotifyBackupsInvitedAsync(backups, comune, request.ResponseDueAt!.Value, cancellationToken);

        logger.LogInformation(
            "ServiceRequest {Id} ({RentalContext}) created by {UserId} for property {PropertyId} booking {BookingId} supplier {SupplierOrgId}",
            request.Id, request.RentalContext, command.UserId, request.PropertyId, request.BookingId, request.SupplierOrgId);

        var created = (await repository.GetByIdAsync(request.Id, cancellationToken))!;

        if (created.AutoAccepted)
        {
            await notifier.NotifyHostAsync(created, cancellationToken);
            await notifier.NotifySupplierAutoAcceptedAsync(created, cancellationToken);
        }

        return created;
    }

    /// <summary>
    /// SP-04d (decision SP-D6): turns the new <paramref name="request"/> into a job already taken when the supplier asked for its
    /// regular hosts to be accepted automatically and the request qualifies (<see cref="ServiceRequestAutoAccept"/>). Called with the
    /// supplier's calendar lock held and the slot and the overlap already checked, so the take is saved by the very transaction that
    /// verified the time: an automatic acceptance never makes two jobs overlap. Does nothing, and the request stays <c>Richiesto</c> as
    /// ever, unless every condition holds; the cheap ones are tried first, so nothing is read for a request that cannot qualify.
    /// </summary>
    private async Task AcceptAutomaticallyIfRegularAsync(
        ServiceRequest request,
        CreateServiceRequestCommand command,
        SupplierProfile supplier,
        SupplierServiceForRequest? service,
        CancellationToken cancellationToken)
    {
        if (!ServiceRequestAutoAccept.IsEligibleRequest(service, command.DurationMinutes))
            return;

        if (!(await agenda.GetSettingsAsync(supplier.OrgId, cancellationToken)).AutoAcceptRegulars)
            return;

        if (SupplierActivationRules.TosState(supplier, legalDocuments.GetTos().Version).BlocksActions)
            return;

        var jobsTaken = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(
                r => r.OrgId == request.OrgId
                    && r.SupplierOrgId == request.SupplierOrgId
                    && r.RentalContext != ServiceRequestRentalContext.Showcase
                    && ServiceRequestAutoAccept.JobStatuses.Contains(r.Status),
                cancellationToken);
        if (!ServiceRequestAutoAccept.IsRegular(jobsTaken, options.Value.RegularHostMinJobs))
            return;

        request.PaymentMode = await ResolvePaymentModeAsync(request, cancellationToken);
        request.Status = ServiceRequestStatus.PresoInCarico;
        request.AutoAccepted = true;
        request.TakenAt = request.CreatedAt;
        request.TakenByUserId = null;

        request.ResponseDueAt = null;

        logger.LogInformation(
            "ServiceRequest {Id}: accepted automatically for a regular host of supplier {SupplierOrgId} ({JobsTaken} jobs taken with it)",
            request.Id, request.SupplierOrgId, jobsTaken);
    }

    private async Task EnsureBookingRuleAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        if (command.RentalContext == ServiceRequestRentalContext.LongRent)
        {
            if (command.BookingId is not null)
            {
                throw new DomainRuleException(
                    ServiceRequestErrorCodes.BookingNotAllowed, ServiceRequestErrorCodes.BookingNotAllowedMessageKey);
            }

            return;
        }

        if (command.BookingId is not { } bookingId)
            return;

        var belongsToProperty = await db.Bookings
            .IgnoreQueryFilters()
            .AnyAsync(
                b => b.Id == bookingId && b.PropertyId == command.PropertyId && b.OrgId == command.OrgId,
                cancellationToken);

        if (!belongsToProperty)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.BookingMismatch, ServiceRequestErrorCodes.BookingMismatchMessageKey);
        }
    }

    /// <summary>
    /// LR-03c: a long-rent request can be made for one lease of its property (the sheet of a contract). The lease must be the host's own and
    /// on the request's property: another org's answers like a missing one (404 <see cref="ServiceRequestErrorCodes.LeaseNotFound"/>), one
    /// of another property is 422 <see cref="ServiceRequestErrorCodes.LeaseMismatch"/>, and a request that is not a long-rent one takes
    /// none (422 <see cref="ServiceRequestErrorCodes.LeaseNotAllowed"/>). A link and nothing more: nothing of the lease, of its tenant or of
    /// its people is read, copied or sent on to the supplier (decision LR-D22).
    /// </summary>
    private async Task EnsureLeaseRuleAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        if (command.LeaseContractId is not { } leaseId)
            return;

        if (command.RentalContext != ServiceRequestRentalContext.LongRent)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.LeaseNotAllowed, ServiceRequestErrorCodes.LeaseNotAllowedMessageKey);
        }

        var leaseProperty = await db.LeaseContracts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(l => l.Id == leaseId && l.OrgId == command.OrgId)
            .Select(l => (Guid?)l.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (leaseProperty is null)
        {
            throw new NotFoundException($"Lease {leaseId} not found for the service request")
            {
                Code = ServiceRequestErrorCodes.LeaseNotFound,
                MessageKey = ServiceRequestErrorCodes.LeaseNotFoundMessageKey,
            };
        }

        if (leaseProperty != command.PropertyId)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.LeaseMismatch, ServiceRequestErrorCodes.LeaseMismatchMessageKey);
        }
    }

    private async Task<SupplierServiceForRequest?> FindRequestableServiceAsync(
        CreateServiceRequestCommand command,
        string category,
        CancellationToken cancellationToken)
    {
        if (command.ServiceListingId is not { } listingId)
            return null;

        var service = await catalog.FindForRequestAsync(command.SupplierOrgId, listingId, cancellationToken)
            ?? throw new NotFoundException($"Service {listingId} not found for the supplier")
            {
                Code = ServiceRequestErrorCodes.ServiceNotFound,
                MessageKey = ServiceRequestErrorCodes.ServiceNotFoundMessageKey,
            };

        if (!service.IsRequestable)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ServiceUnavailable, ServiceRequestErrorCodes.ServiceUnavailableMessageKey);
        }

        if (!string.Equals(service.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ServiceCategoryMismatch, ServiceRequestErrorCodes.ServiceCategoryMismatchMessageKey);
        }

        return service;
    }

    /// <summary>
    /// Every request stores an interval. When the host omitted the start, use Europe/Rome 10:00 on the stay check-out
    /// day (or on today when there is no stay: long-rent, or a short-rent request for the house alone, SR-08a) and a duration from
    /// the host, the catalog, or 120 minutes — never a day-only row. Without a stay the default never falls in the past: once
    /// today's 10:00 has gone by it is 10:00 of the next day.
    /// </summary>
    private async Task<CreateServiceRequestCommand> EnsureTimedCreateCommandAsync(
        CreateServiceRequestCommand command,
        SupplierServiceForRequest? service,
        CancellationToken cancellationToken)
    {
        if (command.ScheduledStartUtc is not null)
            return command;

        DateOnly day;
        var now = Now();
        if (command.BookingId is Guid bookingId)
        {
            var checkout = await db.Bookings.AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(b => (DateTime?)b.CheckOutDate)
                .FirstOrDefaultAsync(cancellationToken);
            day = checkout is { } when ? RomeCalendar.DateInRome(when) : RomeCalendar.DateInRome(now);
        }
        else
        {
            day = RomeCalendar.DateInRome(now);
        }

        var start = RomeCalendar.ToUtc(day, DefaultStartTime);

        if (command.RentalContext == ServiceRequestRentalContext.ShortRent && command.BookingId is null && start <= now)
            start = RomeCalendar.ToUtc(day.AddDays(1), DefaultStartTime);

        var minutes = command.DurationMinutes
            ?? (service?.DurationMinutes is > 0 and var fromService ? fromService : 120);

        return command with { ScheduledStartUtc = start, DurationMinutes = minutes };
    }

    /// <summary>
    /// The interval the host asks for (SP-04): a start is always required (filled by
    /// <see cref="EnsureTimedCreateCommandAsync"/> when omitted). The end is
    /// <see cref="CreateServiceRequestCommand.DurationMinutes"/> when set, else the catalog service duration
    /// (422 <see cref="ServiceRequestErrorCodes.TimeNeedsService"/> when neither gives a length).
    /// </summary>
    private static RequestSchedule ResolveCreateSchedule(CreateServiceRequestCommand command, SupplierServiceForRequest? service)
    {
        if (command.ScheduledStartUtc is not { } requested)
            throw TimeInvalid();

        var start = UtcDateTime.Normalize(requested);
        if (command.DurationMinutes is int minutes)
        {
            if (minutes is < SupplierServiceCatalogLimits.MinDurationMinutes or > SupplierServiceCatalogLimits.MaxDurationMinutes)
                throw TimeInvalid();

            return new RequestSchedule(start, start.AddMinutes(minutes), new SupplierSlotQuery(minutes));
        }

        var query = service?.ToSlotQuery()
            ?? throw new DomainRuleException(
                ServiceRequestErrorCodes.TimeNeedsService, ServiceRequestErrorCodes.TimeNeedsServiceMessageKey);

        return new RequestSchedule(start, start.AddMinutes(query.DurationMinutes), query);
    }

    public async Task<ServiceRequest> TakeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        TakeServiceRequestCommand? command = null,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        EnsureNotOnMeasure(request);

        // A backup closed by the race (another backup accepted first) is told so plainly: 409, and it loses nothing.
        ThrowIfRaceClosed(request);
        EnsureCan(request, ServiceRequestStatus.PresoInCarico, ServiceRequestErrorCodes.CannotTakeMessageKey);

        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);
        var answeredAsBackup = request.GroupRole == ServiceRequestGroupRole.Backup;

        if (command?.QuotedAmountCents is { } quote)
        {
            ServiceRequestPricing.EnsureValidAmount(quote);

            if (IsPaidInsideCasaZen(request) && quote != request.EstimatedAmountCents)
                throw new DomainRuleException(ServicePaymentErrors.AmountLocked, ServicePaymentErrors.AmountLockedMessageKey);
        }

        var schedule = await ResolveTakeScheduleAsync(request, supplierOrgId, command, cancellationToken);

        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, request.GroupId, cancellationToken);
        GroupClosure? closure = null;
        await using (transaction)
        {
            GroupTake? groupTake = null;
            if (request.GroupId is { } groupId)
            {
                groupTake = await RequireGroupTakeAsync(id, groupId, cancellationToken);
                request = groupTake.Request;
            }

            if (schedule is not null)
            {
                await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);
            }
            else if (groupTake is { Request.GroupRole: ServiceRequestGroupRole.Backup, State.Group.SlotPlanned: true })
            {
                var backupSchedule = await ResolveScheduleAsync(
                    request, supplierOrgId, request.ScheduledStartUtc!.Value, request.ScheduledEndUtc, cancellationToken);
                await EnsureSlotFreeAsync(supplierOrgId, backupSchedule, exceptRequestId: request.Id, cancellationToken);
            }

            await EnsureNoOverlappingCommitmentAsync(
                supplierOrgId,
                request.Id,
                schedule?.Start ?? request.ScheduledStartUtc,
                schedule?.End ?? request.ScheduledEndUtc,
                cancellationToken);

            await CaptureHeldMoneyAsync(request, cancellationToken);

            if (groupTake is not null)
                closure = CloseGroupAfterTake(groupTake, Now());

            await TransitionAsync(
                request,
                ServiceRequestStatus.PresoInCarico,
                ServiceRequestErrorCodes.CannotTakeMessageKey,
                r =>
                {
                    r.PaymentMode = paymentMode;
                    r.TakenAt = Now();
                    r.TakenByUserId = userId;
                    if (schedule is not null)
                    {
                        r.ScheduledStartUtc = schedule.Start;
                        r.ScheduledEndUtc = schedule.End;
                    }

                    if (command?.QuotedAmountCents is { } committed)
                        r.QuotedAmountCents = committed;

                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        await RecordReplyAsync(request, dueBeforeAnswer, answeredAsBackup, cancellationToken);

        if (closure is { WinnerIsBackup: true })
            await notifier.NotifyHostBackupTookOverAsync(request, cancellationToken);
        else
            await notifier.NotifyHostAsync(request, cancellationToken);

        if (closure is not null)
            await NotifyClosureAsync(closure, cancellationToken);

        return request;
    }

    private async Task<RequestSchedule?> ResolveTakeScheduleAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        TakeServiceRequestCommand? command,
        CancellationToken cancellationToken)
    {
        if (command?.ScheduledStartUtc is not { } requested)
        {
            if (command?.ScheduledEndUtc is not null)
                throw TimeInvalid();

            return null;
        }

        var start = UtcDateTime.Normalize(requested);
        var end = command.ScheduledEndUtc is { } requestedEnd ? UtcDateTime.Normalize(requestedEnd) : (DateTime?)null;
        if (request.ScheduledStartUtc is { } current)
        {
            if (current == start && (end is null || end == request.ScheduledEndUtc))
                return null;

            throw new DomainRuleException(
                ServiceRequestErrorCodes.TimeAlreadySet, ServiceRequestErrorCodes.TimeAlreadySetMessageKey);
        }

        return await ResolveScheduleAsync(request, supplierOrgId, start, end, cancellationToken);
    }

    private async Task<RequestSchedule> ResolveScheduleAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        DateTime start,
        DateTime? end,
        CancellationToken cancellationToken)
    {
        var service = request.ServiceListingId is { } listingId
            ? await catalog.FindForRequestAsync(supplierOrgId, listingId, cancellationToken)
            : null;

        int minutes;
        if (end is { } explicitEnd)
        {
            var length = explicitEnd - start;
            if (length <= TimeSpan.Zero
                || length.Ticks % TimeSpan.TicksPerMinute != 0
                || length.TotalMinutes > SupplierServiceCatalogLimits.MaxDurationMinutes)
            {
                throw TimeInvalid();
            }

            minutes = (int)length.TotalMinutes;
        }
        else
        {
            minutes = service?.DurationMinutes is > 0 and var duration ? duration : throw TimeInvalid();
        }

        return new RequestSchedule(
            start,
            start.AddMinutes(minutes),
            new SupplierSlotQuery(minutes, service?.MinNoticeHours, service?.WeekdaysMask));
    }

    /// <summary>
    /// Checks, with the supplier's calendar lock held by the caller, that <paramref name="schedule"/> is one of the slots the
    /// planner offers for the supplier (hours, time off, closed days, notice, daily maximum, buffer, what is already booked),
    /// leaving out <paramref name="exceptRequestId"/> (the request being moved must not be in its own way). 409
    /// <see cref="ServiceRequestErrorCodes.SlotUnavailable"/> otherwise.
    /// </summary>
    private async Task EnsureSlotFreeAsync(
        Guid supplierOrgId,
        RequestSchedule schedule,
        Guid? exceptRequestId,
        CancellationToken cancellationToken)
    {
        var day = RomeCalendar.DateInRome(schedule.Start);
        var plans = await agenda.PlanAsync(supplierOrgId, day, day, schedule.Query, exceptRequestId, cancellationToken);

        if (!plans.SelectMany(plan => plan.Slots).Any(slot => slot.StartUtc == schedule.Start))
        {
            logger.LogInformation(
                "Supplier {SupplierOrgId}: the slot at {Start:O} is not free for a service request",
                supplierOrgId, schedule.Start);
            throw new DomainConflictException(
                ServiceRequestErrorCodes.SlotUnavailable, ServiceRequestErrorCodes.SlotUnavailableMessageKey);
        }
    }

    public async Task<ServiceRequest> CompleteAsync(
        Guid id,
        Guid supplierOrgId,
        CompleteServiceRequestCommand? command = null,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        EnsureCan(request, ServiceRequestStatus.Completato, ServiceRequestErrorCodes.CannotCompleteMessageKey);

        var reference = ServiceRequestPricing.ReferenceAmount(request.QuotedAmountCents, request.EstimatedAmountCents);

        var agreedLines = request.QuotedAmountCents is null
            ? null
            : ServiceRequestJson.ReadPriceLines(request.PriceLinesJson).Where(line => line.Kind == ServiceRequestPriceLineKinds.Quote).ToList();
        var price = ServiceRequestPricing.ComposeFinalPrice(
            reference,
            command?.FinalAmountCents,
            command?.Extras,
            request.ServiceNameSnapshot ?? request.Category,
            agreedLines);
        var needsConfirmation = ServiceRequestPricing.ExceedsQuote(
            reference, price.FinalAmountCents, options.Value.FinalAmountTolerancePercent);
        var notes = string.IsNullOrWhiteSpace(command?.Notes) ? null : command.Notes.Trim();

        // SP-15a: a request paid inside CasaZen gets its payment together with the completion (one save), unless the amount still
        // needs the host's confirmation (decision D7) or nothing can be charged online, in which case it falls back to manual.
        var plan = await payments.PlanAsync(request, price.FinalAmountCents, price.Lines, needsConfirmation, cancellationToken);

        try
        {
            await TransitionAsync(
                request,
                ServiceRequestStatus.Completato,
                ServiceRequestErrorCodes.CannotCompleteMessageKey,
                r =>
                {
                    r.CompletionNotes = notes;
                    r.CompletedAt = Now();
                    r.FinalAmountCents = price.FinalAmountCents;
                    r.PriceLinesJson = ServiceRequestJson.Serialize(price.Lines);
                    r.FinalAmountNeedsConfirmation = needsConfirmation;
                    r.PaymentMode = plan.Mode;

                    RecordEvent(DomainEventType.ServiceRequestCompleted, r, ServiceRequestStatus.Completato, ServiceRequestEventPayload.CompletedKeyPrefix);
                },
                cancellationToken);
        }
        catch
        {
            payments.Discard(plan);
            throw;
        }

        await payments.AnnounceAsync(plan, request, cancellationToken);
        await RecordCompletedAsync(request, cancellationToken);
        await notifier.NotifyHostAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectAsync(
        Guid id,
        Guid supplierOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        var wasWaitingBackup = request.GroupRole == ServiceRequestGroupRole.Backup;
        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);

        await TransitionAsync(
            request,
            ServiceRequestStatus.Rifiutato,
            ServiceRequestErrorCodes.CannotRejectMessageKey,
            r =>
            {
                r.RejectionReason = reason.Trim();
                r.ResponseDueAt = null;
                ClearProposal(r);
            },
            cancellationToken);

        await RecordReplyAsync(request, dueBeforeAnswer, wasWaitingBackup, cancellationToken);

        if (!wasWaitingBackup)
            await notifier.NotifyHostAsync(request, cancellationToken);

        await SettleClosedMoneyAsync(request, cancellationToken);

        await ResolveGroupAfterChangeAsync(request.GroupId, cancellationToken);
        return request;
    }

    private static bool IsPaidInsideCasaZen(ServiceRequest request) =>
        request is { RentalContext: ServiceRequestRentalContext.Showcase, PaymentMode: ServiceRequestPaymentMode.Online };

    /// <summary>
    /// Charges the customer's card because the supplier is accepting the request, <b>before</b> it changes state; the caller holds the supplier's
    /// calendar lock, so a request is never accepted with no money behind it. 409 <c>payment_authorization_expired</c> (the block lapsed: it is
    /// released) or <c>payment_capture_failed</c> (the card could not be charged now): the take does not happen. A request that is not paid
    /// inside CasaZen is not touched.
    /// </summary>
    private Task CaptureHeldMoneyAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        funds is not null && IsPaidInsideCasaZen(request) ? funds.CaptureForTakeAsync(request, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// The request closed (refused, cancelled): the block on the customer's card is released, or the money charged is given back in full (the
    /// commission included, decision D6). Called after the closing is saved and never throws — the request stays closed, and the funds job settles
    /// the money on its next run if this cannot.
    /// </summary>
    private Task SettleClosedMoneyAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        funds is not null && (IsPaidInsideCasaZen(request) || request.Kind == ServiceRequestKind.Custom)
            ? funds.SettleClosedRequestAsync(request.Id, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// The offer of a request on measure ended without being taken (the customer turned it down, the supplier took it back or replaced it): the block
    /// on the customer's card, or the PaymentIntent still waiting for a card, is released (SP-10b-b). Never throws — the funds job settles it on its
    /// next run if this cannot — and does nothing for a request that is not on measure or has no such payment.
    /// </summary>
    private Task ReleaseOfferPaymentAsync(Guid requestId, CancellationToken cancellationToken) =>
        funds is null ? Task.CompletedTask : funds.CancelOfferPaymentAsync(requestId, cancellationToken);

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        if (request is null
            || request.OrgId != hostOrgId
            || request.RentalContext == ServiceRequestRentalContext.Showcase
            || !ServiceRequestGroupRules.IsVisibleToHost(request.GroupRole))
        {
            throw RequestNotFound(id);
        }

        if (request.PaymentMode == ServiceRequestPaymentMode.Online)
        {
            throw new DomainRuleException(ServicePaymentErrors.OnlinePayment, ServicePaymentErrors.OnlinePaymentMessageKey);
        }

        await TransitionAsync(
            request,
            ServiceRequestStatus.Pagato,
            ServiceRequestErrorCodes.CannotMarkPaidMessageKey,
            r =>
            {
                r.PaidAt = Now();
                r.PaidBy = ServiceRequestActorParty.Host;
            },
            cancellationToken);

        await notifier.NotifySupplierPaidAsync(request, cancellationToken);
        return request;
    }

    /// <summary>
    /// Moves <paramref name="request"/> to <paramref name="to"/> when <see cref="ServiceRequestStateMachine"/> allows it
    /// (422 <see cref="ServiceRequestErrorCodes.InvalidTransition"/> otherwise), and saves it only if nobody changed the
    /// request since it was read (<c>xmin</c>, A4-19). The loser of two concurrent transitions gets 409
    /// <see cref="ServiceRequestErrorCodes.StateChanged"/>: nothing is saved for it, and since the callers notify only
    /// after this returns, only the winner's email and push are sent.
    /// </summary>
    private async Task TransitionAsync(
        ServiceRequest request,
        ServiceRequestStatus to,
        string refusedMessageKey,
        Action<ServiceRequest> apply,
        CancellationToken cancellationToken)
    {
        var from = request.Status;
        EnsureCan(request, to, refusedMessageKey);

        apply(request);
        request.Status = to;

        ClearQuote(request);
        request.UpdatedAt = Now();

        await SaveAsync(request, $"transition {from} -> {to}", cancellationToken);

        logger.LogInformation("ServiceRequest {Id}: {From} -> {To}", request.Id, from, to);
    }

    /// <summary>422 <see cref="ServiceRequestErrorCodes.InvalidTransition"/> unless the state machine allows <paramref name="to"/>.</summary>
    private static void EnsureCan(ServiceRequest request, ServiceRequestStatus to, string refusedMessageKey)
    {
        if (!ServiceRequestStateMachine.CanTransition(request.Status, to))
            throw new DomainRuleException(ServiceRequestErrorCodes.InvalidTransition, refusedMessageKey);
    }

    /// <summary>
    /// Saves the change of <paramref name="request"/> only if nobody changed the request since it was read (<c>xmin</c>, A4-19);
    /// 409 <see cref="ServiceRequestErrorCodes.StateChanged"/> otherwise, with nothing saved.
    /// </summary>
    private async Task SaveAsync(ServiceRequest request, string operation, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "ServiceRequest {Id}: {Operation} refused, the request changed since it was read", request.Id, operation);
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ServiceCharges.LivePaymentIndexName,
        })
        {
            // SP-15a: a save that creates the payment of the request (the completion, the host confirming the amount) lost the
            // race against another call that created it first. Same answer as the xmin check: the request changed meanwhile.
            logger.LogInformation(
                "ServiceRequest {Id}: {Operation} refused, its payment was created by another call", request.Id, operation);
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
        catch (DbUpdateException ex) when (IsLockConflict(ex))
        {
            // A deadlock or a serialization failure of the database is the same thing for the caller as a request that changed under
            // it: the other operation won, nothing of this one was saved (SP-11: the customer and the supplier act on one request).
            logger.LogInformation(
                "ServiceRequest {Id}: {Operation} refused, the database stopped it ({SqlState})", request.Id, operation, PostgresStateOf(ex));
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
    }

    /// <summary>True for the errors PostgreSQL raises when two transactions get in each other's way: a deadlock and a serialization failure.</summary>
    private static bool IsLockConflict(Exception ex) =>
        PostgresStateOf(ex) is Npgsql.PostgresErrorCodes.DeadlockDetected or Npgsql.PostgresErrorCodes.SerializationFailure;

    private static string? PostgresStateOf(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException postgres)
                return postgres.SqlState;
        }

        return null;
    }

    internal static void ClearProposal(ServiceRequest request)
    {
        request.ProposedStartUtc = null;
        request.ProposedEndUtc = null;
        request.ProposedAt = null;
        request.ProposedByUserId = null;
        request.ProposalMessage = null;
    }

    public Task<ServiceRequest?> GetByIdForHostAsync(
        Guid id,
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        CancellationToken cancellationToken = default)
    {
        if (rentalContext == ServiceRequestRentalContext.Showcase)
            return Task.FromResult<ServiceRequest?>(null);

        var query = ApplyHostScope(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.Id == id && r.RentalContext == rentalContext),
            scope);

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    public Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.SupplierOrgId == supplierOrgId, cancellationToken);

    private static readonly ServiceRequestStatus[] ClosedStatuses =
        [.. Enum.GetValues<ServiceRequestStatus>().Where(ServiceRequestStateMachine.IsFinal)];

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        ServiceRequestHostView? view = null,
        Guid? leaseContractId = null,
        CancellationToken cancellationToken = default)
    {
        if (rentalContext == ServiceRequestRentalContext.Showcase)
            return Task.FromResult<(IReadOnlyList<ServiceRequest> Items, int Total)>(([], 0));

        var query = ApplyHostScope(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.RentalContext == rentalContext),
            scope);

        if (status is not null)
            query = query.Where(r => r.Status == status.Value);

        if (view == ServiceRequestHostView.Open)
            query = query.Where(r => !ClosedStatuses.Contains(r.Status));
        else if (view == ServiceRequestHostView.Closed)
            query = query.Where(r => ClosedStatuses.Contains(r.Status));

        if (propertyId is not null)
            query = query.Where(r => r.PropertyId == propertyId.Value);

        if (bookingId is not null)
            query = query.Where(r => r.BookingId == bookingId.Value);

        if (leaseContractId is not null)
            query = query.Where(r => r.LeaseContractId == leaseContractId.Value);

        return MaterializeServiceRequestPageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForSupplierAsync(
        Guid supplierOrgId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        repository.ListForSupplierAsync(supplierOrgId, openOnly, page, pageSize, cancellationToken);

    /// <summary>
    /// The request for a supplier action (take, complete, reject, start, cancel, propose): 404 when it does not exist, 403 when
    /// it was sent to another supplier, 422 <see cref="ServiceRequestErrorCodes.SupplierNotActive"/> when the acting supplier is
    /// not <see cref="SupplierStatus.Active"/> (SU-12, A4-29): a suspended supplier performs no action, and the check comes
    /// before the state machine so it is told why instead of "invalid transition". The host's own action
    /// (<see cref="MarkPaidAsync"/>) is not a supplier action and does not depend on the supplier's status.
    /// </summary>
    private async Task<ServiceRequest> GetRequestForActiveSupplierOrThrow(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken) ?? throw RequestNotFound(id);

        if (request.SupplierOrgId != supplierOrgId)
            throw new UnauthorizedAccessException($"Service request {id} belongs to another supplier");

        var profile = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => new { sp.Status, sp.TosAcceptedAt, sp.TosVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (profile?.Status != SupplierStatus.Active)
        {
            logger.LogInformation(
                "ServiceRequest {Id}: action refused, supplier {SupplierOrgId} is {SupplierStatus}",
                id, supplierOrgId, profile?.Status.ToString() ?? "without a profile");
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierNotActive, ServiceRequestErrorCodes.SupplierNotActiveMessageKey);
        }

        var tos = SupplierActivationRules.TosState(
            new SupplierProfile { TosAcceptedAt = profile.TosAcceptedAt, TosVersion = profile.TosVersion },
            legalDocuments.GetTos().Version);
        if (tos.BlocksActions)
        {
            logger.LogInformation(
                "ServiceRequest {Id}: action refused, supplier {SupplierOrgId} accepted Terms {AcceptedVersion}, current is {CurrentVersion}",
                id, supplierOrgId, tos.AcceptedVersion, tos.CurrentVersion);
            throw new DomainRuleException(
                SupplierActivation.TosReacceptanceRequiredCode, SupplierActivation.TosReacceptanceRequiredMessageKey);
        }

        return request;
    }

    private async Task<ServiceRequest> GetRequestOfHostOrThrow(Guid id, Guid hostOrgId, CancellationToken cancellationToken)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        if (request is null
            || request.OrgId != hostOrgId
            || request.RentalContext == ServiceRequestRentalContext.Showcase
            || !ServiceRequestGroupRules.IsVisibleToHost(request.GroupRole))
        {
            throw RequestNotFound(id);
        }

        return request;
    }

    private static NotFoundException RequestNotFound(Guid id) => new($"Service request {id} not found")
    {
        Code = ServiceRequestErrorCodes.NotFound,
        MessageKey = ServiceRequestErrorCodes.NotFoundMessageKey,
    };

    private static DomainRuleException TimeInvalid() =>
        new(ServiceRequestErrorCodes.TimeInvalid, ServiceRequestErrorCodes.TimeInvalidMessageKey);

    /// <summary>
    /// 409 <see cref="ServiceRequestErrorCodes.CommitmentConflict"/> when this job's interval overlaps one the supplier
    /// already accepted (<c>PresoInCarico</c> or <c>InCorso</c>). Same-day jobs are allowed when the half-open intervals
    /// <c>[start, end)</c> do not touch. 422 <see cref="ServiceRequestErrorCodes.TimeInvalid"/> when this job has no interval
    /// (a request without start and duration must not exist).
    /// </summary>
    private async Task EnsureNoOverlappingCommitmentAsync(
        Guid supplierOrgId,
        Guid exceptRequestId,
        DateTime? startUtc,
        DateTime? endUtc,
        CancellationToken cancellationToken)
    {
        if (startUtc is not { } start || endUtc is not { } end || end <= start)
            throw TimeInvalid();

        var others = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                && r.Id != exceptRequestId
                && (r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso)
                && r.ScheduledStartUtc != null
                && r.ScheduledEndUtc != null)
            .Select(r => new { Start = r.ScheduledStartUtc!.Value, End = r.ScheduledEndUtc!.Value })
            .ToListAsync(cancellationToken);

        var mine = new SupplierInterval(start, end);
        foreach (var other in others)
        {
            if (other.End <= other.Start)
                continue;

            if (!mine.Overlaps(new SupplierInterval(other.Start, other.End)))
                continue;

            logger.LogInformation(
                "Supplier {SupplierOrgId}: a service request overlaps a job already accepted",
                supplierOrgId);
            throw new DomainConflictException(
                ServiceRequestErrorCodes.CommitmentConflict, ServiceRequestErrorCodes.CommitmentConflictMessageKey);
        }
    }
    private static IQueryable<ServiceRequest> ApplyHostScope(IQueryable<ServiceRequest> query, HostScope scope) =>
        query.Where(r => r.OrgId == scope.OrgId).InScope(scope).OnlyVisibleToHost();

    private static async Task<(IReadOnlyList<ServiceRequest> Items, int Total)> MaterializeServiceRequestPageAsync(
        IQueryable<ServiceRequest> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Opens a READ COMMITTED transaction holding the supplier's calendar lock (<c>SupplierCalendarSync</c>, the lock of the
    /// agenda writes and of the iCal sync), or null outside PostgreSQL. Everything read after it sees what the previous holder
    /// committed, so a slot is judged on the agenda as it is now.
    /// </summary>
    private Task<IDbContextTransaction?> LockSupplierCalendarAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, CalendarSyncService.AvailabilityLock(supplierOrgId));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private void RecordEvent(DomainEventType type, ServiceRequest request, ServiceRequestStatus status, string keyPrefix) =>
        recorder?.Record(type, request.Id, ServiceRequestEventPayload.Of(request, status), ServiceRequestEventPayload.KeyOf(keyPrefix, request.Id), request.OrgId);
}
