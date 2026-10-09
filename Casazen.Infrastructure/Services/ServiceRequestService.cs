using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
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
    TimeProvider? timeProvider = null) : IServiceRequestService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The time asked for a request, with the slot query that checks it.</summary>
    private sealed record RequestSchedule(DateTime Start, DateTime End, SupplierSlotQuery Query);

    public async Task<ServiceRequest> CreateAsync(
        CreateServiceRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        // A request from a supplier's showcase is made by ShowcaseBookingService, from a checked e-mail address, and nowhere else:
        // this is the host's path, which would give it a host org and a property (the table refuses that).
        if (command.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command), command.RentalContext, "A showcase request is not created through the host's path.");
        }

        // Only category codes are stored (SU-03); anything else is a 422 before any lookup.
        var category = ServiceCategories.Require(command.Category);

        // IgnoreQueryFilters (tenant only): scoped by the explicit OrgId check below. command.OrgId comes from
        // OrgContextResolver, which may provision the org after the tenant filter cached a null org. The soft-delete
        // filter stays (PC-05): no new request on a deleted property.
        var property = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == command.PropertyId, cancellationToken);

        // Another org's property is answered exactly like a missing one.
        if (property is null || property.OrgId != command.OrgId)
        {
            throw new NotFoundException($"Property {command.PropertyId} not found for the service request")
            {
                Code = ServiceRequestErrorCodes.PropertyNotFound,
                MessageKey = ServiceRequestErrorCodes.PropertyNotFoundMessageKey,
            };
        }

        await EnsureBookingRuleAsync(command, cancellationToken);

        if (command.ChargeToGuest)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ChargeToGuestNotAllowed, ServiceRequestErrorCodes.ChargeToGuestNotAllowedMessageKey);
        }

        var supplier = await db.SupplierProfiles
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

        // SP-04: the service of the supplier's catalog the request is for, and the time the host picked among its slots.
        var service = await FindRequestableServiceAsync(command, category, cancellationToken);
        var schedule = ResolveCreateSchedule(command, service);

        var now = Now();
        var request = new ServiceRequest
        {
            OrgId = command.OrgId,
            BookingId = command.BookingId,
            RentalContext = command.RentalContext,
            PropertyId = command.PropertyId,
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
            EstimatedAmountCents = service?.EstimatedAmountCents,
            ScheduledStartUtc = schedule?.Start,
            ScheduledEndUtc = schedule?.End,
            // Decision D8: a host's request waits for the supplier's answer for HostResponseMinutes. The deadline is recorded
            // always; only the flag SupplierRequestAutoCancel makes the job act on it.
            ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes),
        };

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not a wrong link.
        var comune = property.City;
        var supplierEmail = notifier.RenderCreatedEmail(request, supplier.LegalName, comune);

        IDbContextTransaction? transaction = schedule is null ? null : await LockSupplierCalendarAsync(command.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            if (schedule is not null)
            {
                // After the lock: a request saved by whoever held it before is seen, so the slot is judged on what is there now.
                await EnsureSlotFreeAsync(command.SupplierOrgId, schedule, exceptRequestId: null, cancellationToken);
            }

            await repository.AddAsync(request, cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        notifier.QueueCreated(request, supplier.Email, supplierEmail, comune);

        logger.LogInformation(
            "ServiceRequest {Id} ({RentalContext}) created by {UserId} for property {PropertyId} booking {BookingId} supplier {SupplierOrgId}",
            request.Id, request.RentalContext, command.UserId, request.PropertyId, request.BookingId, request.SupplierOrgId);

        return (await repository.GetByIdAsync(request.Id, cancellationToken))!;
    }

    /// <summary>
    /// D2 (SU-07): a short-rent request is for one stay, a booking of the request's property in the host's org; a
    /// long-rent request is for the property and takes no booking. 422 with the codes of
    /// <see cref="ServiceRequestErrorCodes"/>; a booking of another property or org answers like a missing one.
    /// </summary>
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
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.BookingRequired, ServiceRequestErrorCodes.BookingRequiredMessageKey);
        }

        // IgnoreQueryFilters: scoped by the explicit OrgId predicate (same reason as the property lookup above).
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
    /// The service of the supplier's catalog a new request is for (SP-04), or null when the request is by category only.
    /// 404 when it is not one of the supplier's (or was deleted); 422 when it is a draft or paused, or of another category.
    /// </summary>
    private async Task<SupplierServiceForRequest?> FindRequestableServiceAsync(
        CreateServiceRequestCommand command,
        string category,
        CancellationToken cancellationToken)
    {
        if (command.ServiceListingId is not { } listingId)
            return null;

        // Another supplier's service is answered exactly like a missing one.
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
    /// The time a host asked for (SP-04): it needs the service of the catalog, whose duration is the length of the work.
    /// 422 <see cref="ServiceRequestErrorCodes.TimeNeedsService"/> otherwise. Null when the host asked for no time.
    /// </summary>
    private static RequestSchedule? ResolveCreateSchedule(CreateServiceRequestCommand command, SupplierServiceForRequest? service)
    {
        if (command.ScheduledStartUtc is not { } requested)
            return null;

        var query = service?.ToSlotQuery()
            ?? throw new DomainRuleException(
                ServiceRequestErrorCodes.TimeNeedsService, ServiceRequestErrorCodes.TimeNeedsServiceMessageKey);

        var start = UtcDateTime.Normalize(requested);
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
        EnsureCan(request, ServiceRequestStatus.PresoInCarico, ServiceRequestErrorCodes.CannotTakeMessageKey);

        if (command?.QuotedAmountCents is { } quote)
            ServiceRequestPricing.EnsureValidAmount(quote);

        var schedule = await ResolveTakeScheduleAsync(request, supplierOrgId, command, cancellationToken);

        // SP-15a: how the request is paid is decided now and kept (Online only if the flag is on and the supplier's Stripe account
        // can take charges and payouts: no payment before the KYC).
        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        IDbContextTransaction? transaction = schedule is null ? null : await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);
        await using (transaction)
        {
            if (schedule is not null)
                await EnsureSlotFreeAsync(supplierOrgId, schedule, exceptRequestId: request.Id, cancellationToken);

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

                    // The supplier answered: no deadline to meet, and a time it proposed earlier is moot.
                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        await notifier.NotifyHostAsync(request, cancellationToken);
        return request;
    }

    /// <summary>
    /// The time the supplier sets when it takes a request (SP-04), or null when it sets none. A request that has a time keeps it
    /// (the supplier proposes another one with <c>propose-time</c>); sending the same time again is not a change.
    /// </summary>
    private async Task<RequestSchedule?> ResolveTakeScheduleAsync(
        ServiceRequest request,
        Guid supplierOrgId,
        TakeServiceRequestCommand? command,
        CancellationToken cancellationToken)
    {
        if (command?.ScheduledStartUtc is not { } requested)
        {
            // An end without a start says nothing.
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

    /// <summary>
    /// The interval and the slot query for a request moved to <paramref name="start"/>: the length is the explicit end, else
    /// the duration of the request's catalog service. 422 <see cref="ServiceRequestErrorCodes.TimeInvalid"/> when neither gives
    /// a length (or the end is not after the start, or the length is not whole minutes).
    /// </summary>
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

        // The final price: the declared total, or the agreed price plus the extras (decision D7 compares it with the quote).
        var reference = ServiceRequestPricing.ReferenceAmount(request.QuotedAmountCents, request.EstimatedAmountCents);
        var price = ServiceRequestPricing.ComposeFinalPrice(
            reference,
            command?.FinalAmountCents,
            command?.Extras,
            request.ServiceNameSnapshot ?? request.Category);
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
                    // What the supplier writes is its own column: the host's notes stay as the host wrote them.
                    r.CompletionNotes = notes;
                    r.CompletedAt = Now();
                    r.FinalAmountCents = price.FinalAmountCents;
                    r.PriceLinesJson = ServiceRequestJson.Serialize(price.Lines);
                    r.FinalAmountNeedsConfirmation = needsConfirmation;
                    r.PaymentMode = plan.Mode;
                },
                cancellationToken);
        }
        catch
        {
            payments.Discard(plan);
            throw;
        }

        // The link goes out after the save, and only the winner of the transition sends it.
        await payments.AnnounceAsync(plan, request, cancellationToken);
        await notifier.NotifyHostAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectAsync(
        Guid id,
        Guid supplierOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // The API requires a reason of at most 500 characters (400 validation_error, A4-18): a blank one is a bug here.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

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

        // A6-08: the host learns of the rejection by email and push, like of the other supplier decisions.
        await notifier.NotifyHostAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        // Another org's request, and one from a supplier's showcase, are answered exactly like a missing one.
        if (request is null || request.OrgId != hostOrgId || request.RentalContext == ServiceRequestRentalContext.Showcase)
            throw RequestNotFound(id);

        // SP-15a, decision D5: a request paid inside CasaZen is paid with the link (the commission is part of it). The host cannot
        // mark it paid by hand; the only exception is the supplier's, traced (api/supplier/requests/{id}/payment/offline).
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

        // SU-09: the supplier learns that the host marked the request as paid (the payment itself is outside CasaZen).
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

    /// <summary>The time the supplier proposed is dropped when the request moves on (taken, rejected, cancelled).</summary>
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
        // ServiceRequest is not tenant-filtered (two parties, see the TN-2 allow-list); host and supplier
        // reads are scoped by the explicit OrgId / SupplierOrgId predicate, never by the included Property.
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

    // IgnoreQueryFilters: the supplier reads the host's Property (another org) through the request;
    // scoped by the explicit SupplierOrgId predicate.
    public Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.SupplierOrgId == supplierOrgId, cancellationToken);

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: scoped by the explicit host OrgId predicate (see GetByIdForHostAsync).
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

        if (propertyId is not null)
            query = query.Where(r => r.PropertyId == propertyId.Value);

        if (bookingId is not null)
            query = query.Where(r => r.BookingId == bookingId.Value);

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

        // 403 (FD-05): the request exists but was sent to another supplier.
        if (request.SupplierOrgId != supplierOrgId)
            throw new UnauthorizedAccessException($"Service request {id} belongs to another supplier");

        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
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

        // SU-05: a new Terms of Service version must be accepted again before the supplier keeps acting on requests.
        // A supplier that accepted before versions were recorded is only asked to accept (it is not blocked).
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

    /// <summary>
    /// The request of the host org (404 <see cref="ServiceRequestErrorCodes.NotFound"/> for a missing one and for another org's,
    /// which answers the same), for the host's own actions (cancel, remind, answer a proposal).
    /// </summary>
    private async Task<ServiceRequest> GetRequestOfHostOrThrow(Guid id, Guid hostOrgId, CancellationToken cancellationToken)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        // The host never reaches a request of a supplier's public showcase (SP-10), even when the supplier org is also its org.
        if (request is null || request.OrgId != hostOrgId || request.RentalContext == ServiceRequestRentalContext.Showcase)
            throw RequestNotFound(id);

        return request;
    }

    private static NotFoundException RequestNotFound(Guid id) => new($"Service request {id} not found")
    {
        Code = ServiceRequestErrorCodes.NotFound,
        MessageKey = ServiceRequestErrorCodes.NotFoundMessageKey,
    };

    private static DomainRuleException TimeInvalid() =>
        new(ServiceRequestErrorCodes.TimeInvalid, ServiceRequestErrorCodes.TimeInvalidMessageKey);

    /// <summary>The host's org and, for a scope bound to an owner, only the requests on that owner's properties.</summary>
    private static IQueryable<ServiceRequest> ApplyHostScope(IQueryable<ServiceRequest> query, HostScope scope)
    {
        query = query.Where(r => r.OrgId == scope.OrgId);
        if (scope.OwnerId is { } ownerId)
            query = query.Where(r => r.Property != null && r.Property.OwnerId == ownerId);

        return query;
    }

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
}
