using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
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
    TimeProvider? timeProvider = null,
    IFeatureFlags? featureFlags = null,
    ServiceRequestBackupSelector? backupSelector = null,
    ISupplierReliabilityService? reliability = null) : IServiceRequestService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The wall clock time (Europe/Rome) a request gets when the host names none (SP-04, SR-08a).</summary>
    private static readonly TimeOnly DefaultStartTime = new(10, 0);

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
        command = await EnsureTimedCreateCommandAsync(command, service, cancellationToken);
        var schedule = ResolveCreateSchedule(command, service);

        // SP-BKP: the backups the same request is sent to, declared as such. Chosen before the lock (a few reads), added to the same save below.
        var backupSuppliers = await SelectBackupsAsync(command, property, category, schedule, cancellationToken);

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
            ScheduledStartUtc = schedule.Start,
            ScheduledEndUtc = schedule.End,
            // Decision D8: a host's request waits for the supplier's answer for HostResponseMinutes. The deadline is recorded
            // always; only the flag SupplierRequestAutoCancel makes the job act on it.
            ResponseDueAt = now.AddMinutes(options.Value.HostResponseMinutes),
        };

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not a wrong link.
        var comune = property.City;
        var supplierEmail = notifier.RenderCreatedEmail(request, supplier.LegalName, comune);

        IReadOnlyList<(ServiceRequest Request, SupplierProfile Supplier)> backups = [];
        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(command.SupplierOrgId, cancellationToken);
        await using (transaction)
        {
            // After the lock: a request saved by whoever held it before is seen, so the slot is judged on what is there now.
            // A duration the host typed is the length of the job; it does not have to be one of the published slots.
            if (command.DurationMinutes is null)
                await EnsureSlotFreeAsync(command.SupplierOrgId, schedule, exceptRequestId: null, cancellationToken);

            await EnsureNoOverlappingCommitmentAsync(
                command.SupplierOrgId, request.Id, schedule.Start, schedule.End, cancellationToken);

            // SP-04d: with the slot checked, the request of a regular host is saved already taken, in this same transaction.
            await AcceptAutomaticallyIfRegularAsync(request, command, supplier, service, cancellationToken);

            // SP-BKP: a request that waits for its supplier's answer is tied to its backups in the same save; one that was accepted by the
            // supplier's own rule has nothing to wait for, so it has no backups. The primary's window is the hour of the rule, not the 120 minutes.
            if (!request.AutoAccepted && backupSuppliers.Count > 0)
                backups = AttachBackupGroup(request, backupSuppliers, slotPlanned: command.DurationMinutes is null, now);

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

        // The request accepted automatically is told like a take, from the request as it is stored (with its property): the host
        // learns it was accepted, the supplier that it was accepted for it. The supplier is not sent the email of a new request.
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
        // A priced service of the catalog whose time the planner checked: a duration the host typed skips that check (see CreateAsync).
        if (!ServiceRequestAutoAccept.IsEligibleRequest(service, command.DurationMinutes))
            return;

        // The supplier's settings are the agenda service's to read (the table is keyed by the supplier org and not tenant-filtered); a
        // supplier that never saved one has the defaults, so the option is off.
        if (!(await agenda.GetSettingsAsync(supplier.OrgId, cancellationToken)).AutoAcceptRegulars)
            return;

        // The gate of a take by hand (SU-05): a supplier with a new version of the Terms to accept performs no action.
        if (SupplierActivationRules.TosState(supplier, legalDocuments.GetTos().Version).BlocksActions)
            return;

        // The jobs this host's org has taken with this supplier. ServiceRequest has two parties and no tenant filter (TN-2 allow-list):
        // scoped by both org ids, and a showcase request (whose OrgId is the supplier's own) is never a host's job.
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

        // Like a take (SP-15a), the way it is paid is decided now; unlike a take, no member of the supplier does it.
        request.PaymentMode = await ResolvePaymentModeAsync(request, cancellationToken);
        request.Status = ServiceRequestStatus.PresoInCarico;
        request.AutoAccepted = true;
        request.TakenAt = request.CreatedAt;
        request.TakenByUserId = null;

        // Answered at once: there is no deadline to meet.
        request.ResponseDueAt = null;

        logger.LogInformation(
            "ServiceRequest {Id}: accepted automatically for a regular host of supplier {SupplierOrgId} ({JobsTaken} jobs taken with it)",
            request.Id, request.SupplierOrgId, jobsTaken);
    }

    /// <summary>
    /// D2 (SU-07): a short-rent request may be for one stay, a booking of the request's property in the host's org (SR-D3, SR-08a:
    /// the stay is optional, a request can be for the house alone); a long-rent request is for the property and takes no booking. 422
    /// with the codes of <see cref="ServiceRequestErrorCodes"/>; a booking of another property or org answers like a missing one.
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

        // No stay named: the request is for the property alone (the database check only asks for a property and the host's source).
        if (command.BookingId is not { } bookingId)
            return;

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

        // A short-rent request for the house alone (SR-08a): "today at 10:00" that has gone by is the next day's.
        if (command.RentalContext == ServiceRequestRentalContext.ShortRent && command.BookingId is null && start <= now)
            start = RomeCalendar.ToUtc(day.AddDays(1), DefaultStartTime);

        // Explicit duration so the planner is not asked for a published slot the host never chose.
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

        // A backup closed by the race (another backup accepted first) is told so plainly: 409, and it loses nothing.
        ThrowIfRaceClosed(request);
        EnsureCan(request, ServiceRequestStatus.PresoInCarico, ServiceRequestErrorCodes.CannotTakeMessageKey);

        // The deadline the request had when the supplier answered: the take clears it, the ranking (SP-REL) needs it. The group role
        // changes with the take (the backup that gets the job is promoted), so it is read now.
        var dueBeforeAnswer = await ReplyDeadlineAsync(request, cancellationToken);
        var answeredAsBackup = request.GroupRole == ServiceRequestGroupRole.Backup;

        if (command?.QuotedAmountCents is { } quote)
            ServiceRequestPricing.EnsureValidAmount(quote);

        var schedule = await ResolveTakeScheduleAsync(request, supplierOrgId, command, cancellationToken);

        // SP-15a: how the request is paid is decided now and kept (Online only if the flag is on and the supplier's Stripe account
        // can take charges and payouts: no payment before the KYC).
        var paymentMode = await ResolvePaymentModeAsync(request, cancellationToken);

        // Always under the calendar lock: two takes of the same day must not both pass the overlap check. A request of a group of backup
        // suppliers (SP-BKP) first takes the lock of its group, always in this order.
        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, request.GroupId, cancellationToken);
        GroupClosure? closure = null;
        await using (transaction)
        {
            // The group is read again under its lock and the take is judged by its rules: the primary has its window, a backup waits for it or
            // takes the job in its turn. From here on the request is the one committed now (the tracker was cleared to read it).
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
                // A backup did not hold the slot: the time the host picked among the primary's slots must be one of its own now that it gets the job.
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

            // The others of the group are closed in the same save as the take: either the job is this supplier's and the rest is over, or nothing.
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

                    // The supplier answered: no deadline to meet, and a time it proposed earlier is moot.
                    r.ResponseDueAt = null;
                    ClearProposal(r);
                },
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        await RecordReplyAsync(request, dueBeforeAnswer, answeredAsBackup, cancellationToken);

        // The host learns who has the job; when a backup took it in place of the primary, it is told so.
        if (closure is { WinnerIsBackup: true })
            await notifier.NotifyHostBackupTookOverAsync(request, cancellationToken);
        else
            await notifier.NotifyHostAsync(request, cancellationToken);

        if (closure is not null)
            await NotifyClosureAsync(closure, cancellationToken);

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

        // The lines of the quote the customer accepted (SP-04b) stay as it approved them while they still are the price of the work itself.
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
        // The API requires a reason of at most 500 characters (400 validation_error, A4-18): a blank one is a bug here.
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

        // Saying no in time is an answer in time (SP-REL): the host can ask somebody else at once. A backup's counts as a backup's.
        await RecordReplyAsync(request, dueBeforeAnswer, wasWaitingBackup, cancellationToken);

        // A6-08: the host learns of the rejection by email and push, like of the other supplier decisions. Not of a backup's: the host did not
        // choose it, and the group goes on without it.
        if (!wasWaitingBackup)
            await notifier.NotifyHostAsync(request, cancellationToken);

        // SP-BKP: a refusal changes the group (the primary is out, or a backup is): it is looked at right away.
        await ResolveGroupAfterChangeAsync(request.GroupId, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        // Another org's request, one from a supplier's showcase and a backup's that waits are answered exactly like a missing one.
        if (request is null
            || request.OrgId != hostOrgId
            || request.RentalContext == ServiceRequestRentalContext.Showcase
            || !ServiceRequestGroupRules.IsVisibleToHost(request.GroupRole))
        {
            throw RequestNotFound(id);
        }

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

        // SP-04b: a quote is an offer on a request that still waits for its answer. Whatever moves the request on (taken, refused,
        // cancelled) takes the offer away with it; the database refuses a quote on a request that is not new.
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

    /// <summary>
    /// The statuses of the closed view of a host (<see cref="ServiceRequestHostView.Closed"/>): the ones nothing leaves, as the state
    /// machine says (<see cref="ServiceRequestStateMachine.IsFinal"/>), in an array the query can turn into an IN list. The open view
    /// is every other status.
    /// </summary>
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

        if (view == ServiceRequestHostView.Open)
            query = query.Where(r => !ClosedStatuses.Contains(r.Status));
        else if (view == ServiceRequestHostView.Closed)
            query = query.Where(r => ClosedStatuses.Contains(r.Status));

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

        // The host never reaches a request of a supplier's public showcase (SP-10), even when the supplier org is also its org. Nor a backup
        // that waits (SP-BKP): it follows the request of the supplier it chose, and the group is reached through it.
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

        // The one definition of "overlap" of the agenda (the console's `overlap` conflict, SP-03b, calls the same method).
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
    /// <summary>The host's org and, for a restricted scope (AM-03), only the requests on the properties the caller reaches.</summary>
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
}
