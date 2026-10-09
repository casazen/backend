using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseBookingService"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ShowcaseBookingHolds</c> and <c>ServiceCustomers</c> are keyed by the supplier org and not
/// tenant-filtered (the TN-2 allow-list says why: the customer is anonymous and a supplier-only account has no
/// <c>User.OrgId</c>). Every statement on them goes through <see cref="HoldsOf"/> / <see cref="CustomerByEmailOf"/>, which carry
/// the explicit <c>OrgId</c> predicate, or is an insert of a row that has its <c>OrgId</c> set;
/// <c>ShowcaseBookingTenancyTests</c> keeps it that way. The supplier is always the one the caller found by its showcase slug and
/// that the public service found <c>Active</c>.</para>
/// <para><b>Concurrency.</b> Both operations run in a READ COMMITTED transaction that takes the supplier's calendar lock
/// (<c>SupplierCalendarSync</c>, shared with the agenda, the iCal sync and the requests with a time), so everything below sees what
/// the previous holder committed: an idempotent replay is recognized, the cap of unverified bookings is counted on the real
/// number, and the slot is judged by the slot planner — <b>without any cache</b>, with the holds in its input — after the lock is
/// taken, which is why of N customers who book the same slot at the same moment exactly one wins. The check of the e-mail takes
/// the same lock, so the hold stops counting and the request starts counting inside one transaction: no moment shows the slot
/// free. Outside PostgreSQL (EF InMemory in unit tests) nothing is locked.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never a name, an address, an e-mail or a token.</para>
/// </remarks>
public sealed class ShowcaseBookingService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    ISupplierComuneMatcher comuneMatcher,
    IServiceCustomerIndex customerIndex,
    ShowcaseBookingNotifier notifier,
    IOptions<ShowcaseBookingOptions> options,
    ILogger<ShowcaseBookingService> logger,
    TimeProvider? timeProvider = null) : IShowcaseBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseBookingHoldResult> CreateHoldAsync(
        SupplierProfile supplier,
        ShowcaseBookingInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(input);

        // The supplier is the one the showcase found Active; checked again because it costs nothing and the rule is worth keeping.
        if (supplier.Status != SupplierStatus.Active)
            throw SupplierNotFound();

        var config = options.Value;
        var privacyVersion = config.CurrentPrivacyNoticeVersion;
        if (privacyVersion is null)
        {
            // The startup refuses this when the flag is on; here it is only a guard that fails closed.
            logger.LogError("Suppliers:Showcase:PrivacyNoticeVersion is not configured: no booking can record its consent");
            throw ShowcaseBookingErrors.OldConsent();
        }

        var content = ShowcaseBookingRules.Normalize(input, privacyVersion);
        var orgId = supplier.OrgId;

        var bookable = await FindServiceAsync(orgId, content.ServiceSlug, cancellationToken);
        var service = bookable.Service;

        var bookingSettings = await agenda.GetBookingSettingsAsync(orgId, cancellationToken);
        if (!bookingSettings.OnlineBookingEnabled)
            throw ShowcaseBookingErrors.OfflineSupplier();

        // What the customer chose has to fit the service; the supplier covers comuni, not postal codes (decision D10).
        var choices = SupplierQuoteCalculator.Validate(service, content.Quote);
        if (!await comuneMatcher.CoversAsync(supplier, new ComuneTarget(content.ComuneIstat, content.City), cancellationToken))
            throw ShowcaseBookingErrors.OutsideSupplierZone();

        // The same function that priced the page prices the booking: the total the customer saw is the total the request carries.
        var quote = SupplierQuoteCalculator.Calculate(service, choices, insideArea: true);
        var query = service.ToSlotQuery() ?? throw ServiceNotFound();
        var start = content.StartUtc;
        var end = start.AddMinutes(query.DurationMinutes);

        var emailHash = customerIndex.HashEmail(content.Email);
        var token = ShowcaseBookingTokens.New();
        var holdId = Guid.NewGuid();
        var validMinutes = config.EmailVerificationMinutes;

        var payload = new ShowcaseBookingPayload(
            bookable.ListingId,
            service.Name,
            service.Category,
            quote.IsEstimate ? quote.TotalCents : null,
            SupplierQuoteCalculator.SnapshotOptions(service, choices),
            content.FullName,
            content.Email,
            content.Phone,
            content.Locale,
            content.ComuneIstat,
            content.City,
            content.PostalCode,
            content.Address,
            content.Floor,
            content.AccessNotes,
            content.PrivacyNoticeVersion,
            DateTime.MinValue,
            content.ConsentIp);

        // Rendered before the hold is saved: a missing App:PublicSiteBaseUrl is a configuration error that has to stop the
        // booking, not a wrong link in an e-mail that holds a slot for nothing.
        var verification = notifier.RenderVerification(supplier, payload, start, holdId, token, validMinutes);

        ShowcaseBookingHoldResult result;
        ShowcaseBookingHold created;
        await using (var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken))
        {
            // Everything from here on is read after the lock: what the previous holder committed is there.
            var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

            var existing = await HoldsOf(db, orgId)
                .Where(h => h.ClientRequestId == content.ClientRequestId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                // The same client request id is the same booking: a retry after a lost answer takes no second slot and sends no
                // second e-mail. A hold that lapsed (and is not yet deleted by the upkeep job) is out of the way instead: the
                // unique index would refuse the new one, and it holds nothing anymore.
                if (existing.ExpiresAt > now || existing.ConsumedAt is not null)
                {
                    logger.LogInformation("Showcase hold {HoldId} of supplier {OrgId}: replay of the same client request", existing.Id, orgId);
                    return new ShowcaseBookingHoldResult(existing.Id, existing.ExpiresAt);
                }

                db.ShowcaseBookingHolds.Remove(existing);
                await SaveAsync(cancellationToken);
            }

            // At most three bookings of one address wait for the check at the same time: the fourth waits for one to be checked or
            // to lapse. It is a 429 like the limits per IP and per address, so the three look alike.
            var waiting = await HoldsOf(db, orgId)
                .Where(h => h.EmailHash == emailHash && h.ConsumedAt == null && h.ExpiresAt > now)
                .OrderBy(h => h.ExpiresAt)
                .Select(h => h.ExpiresAt)
                .ToListAsync(cancellationToken);
            if (waiting.Count >= ShowcaseBookingLimits.MaxUnverifiedHoldsPerEmail)
            {
                logger.LogInformation("Showcase hold of supplier {OrgId} refused: the address already has the most it may have", orgId);
                throw new ShowcaseBookingTooManyHoldsException(waiting[0] - now);
            }

            // The slot is judged here, by the planner, on the agenda as it is now: requests with hours, blocks, calendar
            // engagements and the holds of every other customer. No cache: the one of the public slots is advisory.
            var day = RomeCalendar.DateInRome(start);
            var plans = await agenda.PlanAsync(orgId, day, day, query, cancellationToken);
            if (!plans.SelectMany(plan => plan.Slots).Any(slot => slot.StartUtc == start))
            {
                logger.LogInformation("Supplier {OrgId}: the slot at {Start:O} is not free for a showcase booking", orgId, start);
                throw SlotUnavailable();
            }

            var publicCode = await NewPublicCodeAsync(orgId, cancellationToken);
            payload = payload with { PrivacyAcceptedAt = now };

            var hold = new ShowcaseBookingHold
            {
                Id = holdId,
                OrgId = orgId,
                ClientRequestId = content.ClientRequestId,
                StartUtc = start,
                EndUtc = end,
                PublicCode = publicCode,
                TokenHash = ShowcaseBookingTokens.Hash(token),
                EmailHash = emailHash,
                Payload = payload.ToJson(),
                ExpiresAt = now.AddMinutes(validMinutes),
                CreatedAt = now,
            };
            db.ShowcaseBookingHolds.Add(hold);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            created = hold;
            result = new ShowcaseBookingHoldResult(hold.Id, hold.ExpiresAt);
        }

        logger.LogInformation("Showcase hold {HoldId} of supplier {OrgId} created", result.Id, orgId);

        // After the commit: an e-mail for a booking that was not saved would be a link to nothing.
        if (!notifier.QueueVerification(content.Email, verification))
        {
            // The customer cannot be written to (provider not configured, queue down): a hold nobody can check would keep the
            // slot of the supplier for the minutes it lasts and the customer would wait for an e-mail that never comes. Nobody
            // has the token yet, so nothing can be checking it: the hold goes, and the customer can try again.
            await ReleaseAsync(created, cancellationToken);
            logger.LogError("Showcase hold {HoldId} of supplier {OrgId} released: the verification e-mail could not be queued", result.Id, orgId);
            throw new EmailConfigurationException("The verification e-mail of a showcase booking could not be queued.");
        }

        return result;
    }

    /// <summary>Deletes a hold nobody can check (see <see cref="CreateHoldAsync"/>); a failure here only leaves it to lapse.</summary>
    private async Task ReleaseAsync(ShowcaseBookingHold hold, CancellationToken cancellationToken)
    {
        try
        {
            db.ShowcaseBookingHolds.Remove(hold);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Showcase hold {HoldId} could not be released: it lapses by itself", hold.Id);
        }
    }

    public async Task<ShowcaseBookingConfirmation> ConfirmEmailAsync(
        SupplierProfile supplier,
        Guid holdId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        var orgId = supplier.OrgId;

        ServiceRequest request;
        ServiceCustomer customer;
        ShowcaseBookingConfirmation result;
        await using (var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken))
        {
            var hold = await HoldsOf(db, orgId).Where(h => h.Id == holdId).FirstOrDefaultAsync(cancellationToken);

            // One answer for a booking that does not exist, one of another supplier and a wrong token.
            if (hold is null || !ShowcaseBookingTokens.Matches(hold.TokenHash, token))
                throw ShowcaseBookingErrors.InvalidLink();

            var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

            // A second click on the link (or a retry) answers the same, with the request as it is by then. Nothing happens again.
            if (hold.ConsumedAt is not null)
                return await ReplayAsync(hold, orgId, cancellationToken);

            if (hold.ExpiresAt <= now)
                throw ShowcaseBookingErrors.ExpiredLink();

            // A supplier suspended meanwhile takes no new request (the profile is read again under the lock).
            var status = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == orgId)
                .Select(sp => (SupplierStatus?)sp.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (status != SupplierStatus.Active)
                throw ShowcaseBookingErrors.InactiveSupplier();

            var payload = ShowcaseBookingPayload.FromJson(hold.Payload);
            if (payload is null)
            {
                logger.LogError("Showcase hold {HoldId} of supplier {OrgId} has no readable payload", hold.Id, orgId);
                throw ShowcaseBookingErrors.InvalidLink();
            }

            var settings = await agenda.GetBookingSettingsAsync(orgId, cancellationToken);
            customer = await UpsertCustomerAsync(orgId, hold, payload, now, cancellationToken);

            request = new ServiceRequest
            {
                OrgId = orgId,
                SupplierOrgId = orgId,
                RentalContext = ServiceRequestRentalContext.Showcase,
                Source = ServiceRequestSource.Showcase,
                PropertyId = null,
                BookingId = null,
                Category = payload.Category,
                Urgency = ServiceRequestUrgency.Normal,
                Notes = string.Empty,
                Status = ServiceRequestStatus.Richiesto,
                CreatedAt = now,
                UpdatedAt = now,
                ServiceListingId = payload.ServiceListingId,
                ServiceNameSnapshot = payload.ServiceName,
                OptionsJson = ServiceRequestJson.Serialize(payload.Options),
                EstimatedAmountCents = payload.EstimatedAmountCents,
                ScheduledStartUtc = hold.StartUtc,
                ScheduledEndUtc = hold.EndUtc,
                // Decision D8: the supplier answers within SupplierSettings.RespondWithinMinutes (180 by default), or the request
                // lapses (the always-on service-request-expiry job).
                ResponseDueAt = now.AddMinutes(settings.RespondWithinMinutes),
                PublicCode = hold.PublicCode,
                CustomerId = customer.Id,
                LocationComuneIstat = payload.ComuneIstat,
                LocationCity = payload.City,
                LocationPostalCode = payload.PostalCode,
                LocationAddress = payload.Address,
                LocationFloor = payload.Floor,
                LocationAccessNotes = payload.AccessNotes,
            };
            db.ServiceRequests.Add(request);

            // The hold becomes the request in the same save: it stops counting for the planner as the request starts counting.
            // The payload goes: the data moved to the customer and the request. The token hash stays for the replay.
            hold.ConsumedAt = now;
            hold.ServiceRequestId = request.Id;
            hold.Payload = null;

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            result = new ShowcaseBookingConfirmation(
                request.PublicCode!,
                request.Status,
                payload.ServiceName,
                hold.StartUtc,
                hold.EndUtc,
                request.ResponseDueAt,
                AlreadyConfirmed: false);
        }

        logger.LogInformation("Showcase request {RequestId} of supplier {OrgId} created from hold {HoldId}", request.Id, orgId, holdId);

        notifier.QueueRequestReceived(supplier, request, customer, request.ResponseDueAt!.Value);
        return result;
    }

    /// <summary>
    /// The holds of <paramref name="orgId"/>. <b>The only way</b> this service reads or changes a hold: the table is not
    /// tenant-filtered, so the supplier org is always an explicit predicate. Static and internal so a test can read the SQL it
    /// becomes on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<ShowcaseBookingHold> HoldsOf(AppDbContext db, Guid orgId) =>
        db.ShowcaseBookingHolds.Where(h => h.OrgId == orgId);

    /// <summary>
    /// The customer of <paramref name="orgId"/> with the e-mail index <paramref name="emailHash"/> (at most one: they are unique
    /// together). Static and internal like <see cref="HoldsOf"/>.
    /// </summary>
    internal static IQueryable<ServiceCustomer> CustomerByEmailOf(AppDbContext db, Guid orgId, string emailHash) =>
        db.ServiceCustomers.Where(c => c.OrgId == orgId && c.EmailHash == emailHash);

    /// <summary>The service as the booking needs it (id included), or the 404 the public gives for a service that is not published.</summary>
    private async Task<SupplierBookableService> FindServiceAsync(Guid orgId, string slug, CancellationToken cancellationToken)
    {
        var normalized = SupplierShowcaseSlug.Normalize(slug);
        if (!SupplierShowcaseSlug.IsLookupable(normalized, SupplierServiceCatalogLimits.SlugMaxLength))
            throw ServiceNotFound();

        return await catalog.FindBookableAsync(orgId, normalized, cancellationToken) ?? throw ServiceNotFound();
    }

    /// <summary>The customer of this address for this supplier, created or brought up to date with what the customer just wrote.</summary>
    private async Task<ServiceCustomer> UpsertCustomerAsync(
        Guid orgId,
        ShowcaseBookingHold hold,
        ShowcaseBookingPayload payload,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var customer = await CustomerByEmailOf(db, orgId, hold.EmailHash).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            customer = new ServiceCustomer
            {
                OrgId = orgId,
                EmailHash = hold.EmailHash,
                CreatedAt = now,
            };
            db.ServiceCustomers.Add(customer);
        }
        else if (!ServiceCustomerEmails.SameAddress(customer.Email, payload.Email))
        {
            // The index says it is the same address and the address says it is not: a collision or a key that changed under
            // existing customers. A request must never be attached to somebody else: stop, loudly.
            logger.LogError("Customer {CustomerId} of supplier {OrgId} has the e-mail index of another address", customer.Id, orgId);
            throw new InvalidOperationException("The e-mail index of a customer does not match its address.");
        }

        customer.FullName = payload.FullName;
        customer.Email = payload.Email;
        customer.Phone = payload.Phone;
        customer.Locale = ServiceCustomerLocales.Normalize(payload.Locale);
        customer.PrivacyNoticeVersion = payload.PrivacyNoticeVersion;
        customer.PrivacyAcceptedAt = payload.PrivacyAcceptedAt;
        customer.ConsentIp = payload.ConsentIp;
        customer.UpdatedAt = now;
        return customer;
    }

    /// <summary>What a second check of the same link answers: the booking, with the request as it is now.</summary>
    private async Task<ShowcaseBookingConfirmation> ReplayAsync(ShowcaseBookingHold hold, Guid orgId, CancellationToken cancellationToken)
    {
        // ServiceRequest is not tenant-filtered (two parties, TN-2 allow-list); scoped by the explicit supplier org.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == hold.ServiceRequestId && r.SupplierOrgId == orgId)
            .Select(r => new { r.PublicCode, r.Status, r.ServiceNameSnapshot, r.ScheduledStartUtc, r.ScheduledEndUtc, r.ResponseDueAt })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ShowcaseBookingErrors.InvalidLink();

        return new ShowcaseBookingConfirmation(
            request.PublicCode ?? hold.PublicCode,
            request.Status,
            request.ServiceNameSnapshot ?? string.Empty,
            request.ScheduledStartUtc ?? hold.StartUtc,
            request.ScheduledEndUtc ?? hold.EndUtc,
            request.Status == ServiceRequestStatus.Richiesto ? request.ResponseDueAt : null,
            AlreadyConfirmed: true);
    }

    /// <summary>
    /// A code no hold and no request of the supplier has. 50 random bits: the first try is enough; the check is the guarantee,
    /// under the lock that every hold and every request of the supplier is created under.
    /// </summary>
    private async Task<string> NewPublicCodeAsync(Guid orgId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ShowcaseBookingLimits.PublicCodeAttempts; attempt++)
        {
            var code = BookingCodes.New();
            var taken = await HoldsOf(db, orgId).AnyAsync(h => h.PublicCode == code, cancellationToken)
                        || await db.ServiceRequests.IgnoreQueryFilters()
                            .AnyAsync(r => r.SupplierOrgId == orgId && r.PublicCode == code, cancellationToken);
            if (!taken)
                return code;
        }

        throw new InvalidOperationException("No free booking code found.");
    }

    /// <summary>
    /// Saves, turning a lost race into a 409: the unique index of a booking, or a state that changed under a request. Under the
    /// supplier's calendar lock neither should happen; this is the guarantee that it is never a 500 if it does.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
        catch (DbUpdateException ex) when (IsConflict(ex))
        {
            logger.LogInformation("A showcase booking lost a race on the database: {SqlState}", PostgresError(ex)?.SqlState);
            throw SlotUnavailable();
        }
    }

    private static bool IsConflict(Exception ex) =>
        PostgresError(ex)?.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected;

    private static PostgresException? PostgresError(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        return null;
    }

    private Task<IDbContextTransaction?> LockSupplierCalendarAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, CalendarSyncService.AvailabilityLock(orgId));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private static NotFoundException SupplierNotFound() =>
        new("Supplier showcase not found") { Code = ProblemNotFound, MessageKey = "SupplierShowcaseNotFound" };

    private static NotFoundException ServiceNotFound() =>
        new("Supplier service not found")
        {
            Code = SupplierServiceCatalogErrors.NotFound,
            MessageKey = "SupplierServiceNotFound",
        };

    private static DomainConflictException SlotUnavailable() =>
        new(ServiceRequestErrorCodes.SlotUnavailable, ServiceRequestErrorCodes.SlotUnavailableMessageKey);

    /// <summary>The generic code of a 404 (the web layer's <c>ProblemCodes.NotFound</c>, which Infrastructure cannot name).</summary>
    private const string ProblemNotFound = "not_found";
}
