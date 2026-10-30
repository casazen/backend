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
/// <para><b>Money (SP-15c-a).</b> A booking with a total is paid inside CasaZen when <see cref="ISupplierFundsService.CanAuthorizeAsync"/>
/// says so (flag on, supplier's account ready): after the hold is saved a PaymentIntent blocks the customer's card (Stripe is never called
/// inside the calendar lock), and the request is created only when <b>both</b> the e-mail is checked and the block succeeded, in either order, by
/// the same method under the same lock (<see cref="CreateRequestFromHoldAsync"/>), together with its <c>Authorized</c> payment. A hold that
/// lapses first cancels its PaymentIntent (<see cref="ReleaseExpiredHoldAsync"/>): the customer's card is never left blocked for a request that
/// does not exist, and no request exists without a block. Without the funds service, or when it says no, nothing changes: the booking is made
/// as it always was.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never a name, an address, an e-mail or a token.</para>
/// </remarks>
public sealed class ShowcaseBookingService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    ISupplierComuneMatcher comuneMatcher,
    IComuneDirectory comuneDirectory,
    IServiceCustomerIndex customerIndex,
    ShowcaseBookingNotifier notifier,
    IOptions<ShowcaseBookingOptions> options,
    ILogger<ShowcaseBookingService> logger,
    TimeProvider? timeProvider = null,
    ISupplierFundsService? funds = null) : IShowcaseBookingService
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

        // An urgent slot (SP-04c) is for a service and a supplier that both accept urgent requests: the terms (and the surcharge the
        // estimate adds) are worked out from the supplier's settings, read only for an urgent booking. A booking that is not urgent
        // reads nothing more than before.
        if (content.Quote.Urgent)
            service = service.WithUrgent(await agenda.GetUrgentAsync(orgId, cancellationToken));

        // What the customer chose has to fit the service; the supplier covers comuni, not postal codes (decision D10). An urgent slot
        // for a service that does not offer one is a 422 on the field `urgent`, like in the estimate.
        var choices = SupplierQuoteCalculator.Validate(service, content.Quote);
        if (!await comuneMatcher.CoversAsync(supplier, new ComuneTarget(content.ComuneIstat, content.City), cancellationToken))
            throw ShowcaseBookingErrors.OutsideSupplierZone();

        // With the code of the comune the supplier reads the official name, not the text typed next to it, which decided nothing
        // once the code covered the zone: it reaches the supplier before the take, in an e-mail sent by CasaZen.
        var city = await ComuneNameAsync(content.ComuneIstat, content.City, cancellationToken);

        // The same function that priced the page prices the booking: the total the customer saw is the total the request carries.
        var quote = SupplierQuoteCalculator.Calculate(service, choices, insideArea: true);

        // SP-15c-a: a booking with a total is paid inside CasaZen when the flag is on and the supplier can be paid: the customer's card is
        // blocked for that total, and the request exists only once the block succeeded as well. Decided here, once. A booking on quote has no
        // total to block and is made as it always was.
        var blockCents = quote.IsEstimate ? quote.TotalCents : null;
        var paidInside = funds is not null && blockCents is { } total && await funds.CanAuthorizeAsync(orgId, total, cancellationToken);

        // The slot is judged below, under the calendar lock, by the planner of the kind the customer chose: the urgent plan for an
        // urgent booking (its window, 30 minutes of notice, today and tomorrow), the normal one otherwise. The lock is the same for
        // both, so an urgent and a normal booking of the same hour cannot both pass: the second finds the hold of the first.
        var query = service.ToSlotQuery(choices.Urgent) ?? throw ServiceNotFound();
        var start = content.StartUtc;

        // A start the planner could never offer — in the past, beyond the largest horizon, or at an end of the calendar, where the
        // date arithmetic of the planner would overflow — is a slot that is not free: the same 409, with nothing computed from it.
        if (!ShowcaseBookingRules.IsWithinSlotWindow(start, _clock.GetUtcNow().UtcDateTime))
        {
            logger.LogInformation("Supplier {OrgId}: a showcase booking asked for a time no slot can have", orgId);
            throw SlotUnavailable();
        }

        var end = start.AddMinutes(query.DurationMinutes);

        var emailHash = customerIndex.HashEmail(content.Email);
        var token = ShowcaseBookingTokens.New();
        var holdId = Guid.NewGuid();
        var validMinutes = config.EmailVerificationMinutes;
        if (choices.Urgent)
        {
            // An urgent slot is 30 minutes away at the soonest: the hold and its link never outlive the start of the work (a request
            // born after the time it is for would be no use to anybody). With the 30 minutes of the configuration, nothing changes.
            var minutesToStart = (int)Math.Floor((start - _clock.GetUtcNow().UtcDateTime).TotalMinutes);
            validMinutes = Math.Clamp(minutesToStart, 1, validMinutes);
        }

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
            city,
            content.PostalCode,
            content.Address,
            content.Floor,
            content.AccessNotes,
            content.PrivacyNoticeVersion,
            DateTime.MinValue,
            content.ConsentIp,
            choices.Urgent);

        // Rendered before the hold is saved: a missing App:PublicSiteBaseUrl is a configuration error that has to stop the
        // booking, not a wrong link in an e-mail that holds a slot for nothing.
        var verification = notifier.RenderVerification(supplier, payload, start, holdId, token, validMinutes);

        // A retry of a booking whose card was already asked for: its block is read from Stripe here, before the lock, so that a call to Stripe
        // never waits inside the supplier's calendar lock (the replay below only copies what was read).
        HoldAuthorization? replayedBlock = null;
        if (paidInside && funds is not null)
        {
            var knownIntent = await HoldsOf(db, orgId)
                .AsNoTracking()
                .Where(h => h.ClientRequestId == content.ClientRequestId && h.ConsumedAt == null)
                .Select(h => h.StripePaymentIntentId)
                .FirstOrDefaultAsync(cancellationToken);
            if (knownIntent is not null)
                replayedBlock = await funds.ReadHoldAuthorizationAsync(knownIntent, cancellationToken);
        }

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
                    return new ShowcaseBookingHoldResult(existing.Id, existing.ExpiresAt, PaymentOf(existing, replayedBlock));
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

        // SP-15c-a: the customer's card is blocked for the total, after the hold is saved and outside the lock. If Stripe cannot do it the hold
        // goes (a StripeException, 503 for the caller) and the e-mail is never sent: nobody is asked to check a booking that cannot be paid.
        if (paidInside && blockCents is { } amountToBlock)
            result = result with { Payment = await BlockCardAsync(created, service.Name, amountToBlock, cancellationToken) };

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

    /// <summary>
    /// Deletes a hold nobody can check (see <see cref="CreateHoldAsync"/>), and the block on the card it asked for if one was made; a failure
    /// here only leaves it to lapse (and a hold that lapses releases its block, <see cref="ReleaseExpiredHoldAsync"/>).
    /// </summary>
    private async Task ReleaseAsync(ShowcaseBookingHold hold, CancellationToken cancellationToken)
    {
        // The customer was never given the secret of this block (the caller's answer is an error), so nobody can confirm it with a card: letting
        // it go is tidiness, not safety, and a failure of Stripe here does not stop the hold from going.
        if (hold.StripePaymentIntentId is { } intent && funds is not null)
        {
            try
            {
                await funds.CancelHoldAuthorizationAsync(hold.Id, intent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Showcase hold {HoldId}: the block {PaymentIntentId} could not be released now", hold.Id, intent);
            }
        }

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

    /// <summary>
    /// Blocks the card of the customer for a hold that was just saved (SP-15c-a): the PaymentIntent is created on the platform and recorded on the
    /// hold <b>before</b> the customer can see its secret, so the webhook that follows the card finds the hold. If Stripe cannot create it, or the hold
    /// cannot record it, the hold goes and the exception reaches the caller (503 or 500): nothing was asked of the customer and no e-mail is sent.
    /// </summary>
    private async Task<ShowcaseBookingPayment> BlockCardAsync(
        ShowcaseBookingHold hold,
        string serviceName,
        int amountCents,
        CancellationToken cancellationToken)
    {
        HoldAuthorization block;
        try
        {
            block = await funds!.AuthorizeHoldAsync(
                new HoldAuthorizationRequest(hold.Id, hold.OrgId, amountCents, serviceName), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Showcase hold {HoldId} of supplier {OrgId} released: the card block could not be created", hold.Id, hold.OrgId);
            await ReleaseAsync(hold, cancellationToken);
            throw;
        }

        // The PaymentIntent and its amount are recorded together (the table checks that they are), before the secret leaves.
        hold.StripePaymentIntentId = block.PaymentIntentId;
        hold.PaymentAmountCents = amountCents;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Showcase hold {HoldId} of supplier {OrgId} released: its card block could not be recorded", hold.Id, hold.OrgId);
            await ReleaseAsync(hold, cancellationToken);
            throw;
        }

        return new ShowcaseBookingPayment(amountCents, block.Currency, block.ClientSecret, block.PublishableKey, Authorized: false);
    }

    /// <summary>
    /// The block of a hold as the answer of a retry shows it: the amount and whether the card is blocked already, with the secret read from
    /// Stripe before the lock (<c>null</c> when the card can no longer be asked for it). <c>null</c> for a hold that is not paid inside CasaZen.
    /// </summary>
    private static ShowcaseBookingPayment? PaymentOf(ShowcaseBookingHold hold, HoldAuthorization? block) =>
        hold.StripePaymentIntentId is null || hold.PaymentAmountCents is not { } amount
            ? null
            : new ShowcaseBookingPayment(
                amount,
                ServiceCharges.Currency.ToUpperInvariant(),
                block?.ClientSecret,
                block?.PublishableKey,
                Authorized: hold.PaymentAuthorizedAt is not null);

    public async Task<ShowcaseBookingConfirmation> ConfirmEmailAsync(
        SupplierProfile supplier,
        Guid holdId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        var orgId = supplier.OrgId;

        BornRequest born;
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

            // SP-15c-a: the address is checked now. A booking whose card is to be blocked becomes a request only when the block has succeeded
            // too, in either order; until then the supplier is told nothing, and the page is told the booking waits for the card. The webhook of
            // the block (ApplyHoldAuthorizationAsync) creates the request under this same lock when it comes.
            var alreadyChecked = hold.EmailVerifiedAt is not null;
            hold.EmailVerifiedAt ??= now;
            if (hold.StripePaymentIntentId is not null && hold.PaymentAuthorizedAt is null)
            {
                await SaveAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);

                logger.LogInformation("Showcase hold {HoldId} of supplier {OrgId}: the e-mail is checked, the request waits for the card block", holdId, orgId);
                return new ShowcaseBookingConfirmation(
                    hold.PublicCode,
                    ServiceRequestStatus.Richiesto,
                    payload.ServiceName,
                    hold.StartUtc,
                    hold.EndUtc,
                    null,
                    alreadyChecked,
                    AwaitingPayment: true);
            }

            born = await CreateRequestFromHoldAsync(orgId, hold, payload, now, cancellationToken);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            result = new ShowcaseBookingConfirmation(
                born.Request.PublicCode!,
                born.Request.Status,
                payload.ServiceName,
                hold.StartUtc,
                hold.EndUtc,
                born.Request.ResponseDueAt,
                AlreadyConfirmed: false);
        }

        logger.LogInformation("Showcase request {RequestId} of supplier {OrgId} created from hold {HoldId}", born.Request.Id, orgId, holdId);

        await notifier.QueueRequestReceivedAsync(supplier, born.Request, born.Customer, born.Request.ResponseDueAt!.Value, cancellationToken);
        return result;
    }

    public async Task<ShowcaseHoldAuthorizationResult> ApplyHoldAuthorizationAsync(
        ShowcaseHoldAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var orgId = authorization.SupplierOrgId;
        var intent = authorization.Observed.PaymentIntentId;

        BornRequest born;
        SupplierProfile supplier;
        await using (var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken))
        {
            // Read after the lock: whoever held it before is seen. The hold is the one the PaymentIntent was made for, by the supplier org in
            // its metadata and the PaymentIntent recorded on the hold; anything else is not the block of a waiting hold.
            var hold = await HoldsOf(db, orgId).Where(h => h.Id == authorization.HoldId).FirstOrDefaultAsync(cancellationToken);
            if (hold is null
                || hold.ConsumedAt is not null
                || !string.Equals(hold.StripePaymentIntentId, intent, StringComparison.Ordinal))
            {
                return ShowcaseHoldAuthorizationResult.NotAHold;
            }

            var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

            // What Stripe says must be what the hold asked for: the platform's own PaymentIntent, the whole total blocked, nothing charged, no
            // fee. Otherwise no request is created from it and the block goes with the hold when it lapses.
            var mismatches = HeldChargeVerification.AuthorizationMismatches(
                new HeldChargeExpectation(hold.StripePaymentIntentId, hold.PaymentAmountCents ?? 0, ServiceCharges.Currency),
                authorization.Observed);
            if (mismatches.Count > 0)
            {
                logger.LogError(
                    "Showcase hold {HoldId} of supplier {OrgId}: the card block {PaymentIntentId} differs from what was asked for ({Mismatches}); no request is created from it",
                    hold.Id, orgId, intent, ServiceChargeVerification.ReviewCode(mismatches));
                return new ShowcaseHoldAuthorizationResult(Handled: true);
            }

            // Recorded once. Stripe's time of the block is used unless it is in the future (a clock that runs behind ours).
            hold.PaymentAuthorizedAt ??= authorization.AuthorizedAt is { } at && at < now ? UtcDateTime.TruncateToMicroseconds(at) : now;

            var payload = hold.EmailVerifiedAt is null ? null : ShowcaseBookingPayload.FromJson(hold.Payload);
            var status = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == orgId)
                .Select(sp => (SupplierStatus?)sp.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (payload is null || hold.ExpiresAt <= now || status != SupplierStatus.Active)
            {
                // The e-mail is still to be checked (the check will create the request), or the hold lapsed, or the supplier is not active
                // anymore: the block is recorded and nothing else happens. A hold that lapsed releases its block with the next run.
                await SaveAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                logger.LogInformation(
                    "Showcase hold {HoldId} of supplier {OrgId}: the card is blocked, {Next}",
                    hold.Id, orgId, hold.EmailVerifiedAt is null ? "the request waits for the e-mail" : "no request is created");
                return new ShowcaseHoldAuthorizationResult(Handled: true);
            }

            // The e-mail was checked first: the block completes the booking now, exactly as the check of the e-mail does when it comes last.
            born = await CreateRequestFromHoldAsync(orgId, hold, payload, now, cancellationToken);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            supplier = await db.SupplierProfiles.AsNoTracking().Where(sp => sp.OrgId == orgId).FirstAsync(cancellationToken);
        }

        logger.LogInformation("Showcase request {RequestId} of supplier {OrgId} created from hold {HoldId} by the card block", born.Request.Id, orgId, authorization.HoldId);
        return new ShowcaseHoldAuthorizationResult(Handled: true, new ShowcaseHoldCompletion(supplier, born.Request, born.Customer));
    }

    public async Task CompleteHoldAuthorizationAsync(ShowcaseHoldCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // The notifier never throws: the request exists, the customer's receipt and the supplier's news are a courtesy that must not undo it.
        if (completion.Request.ResponseDueAt is { } respondBy)
            await notifier.QueueRequestReceivedAsync(completion.Supplier, completion.Request, completion.Customer, respondBy, cancellationToken);
    }

    public async Task<bool> ReleaseExpiredHoldAsync(Guid supplierOrgId, Guid holdId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockSupplierCalendarAsync(supplierOrgId, cancellationToken);

        var hold = await HoldsOf(db, supplierOrgId).Where(h => h.Id == holdId).FirstOrDefaultAsync(cancellationToken);
        if (hold is null)
            return true;

        // Read after the lock: a hold that became a request meanwhile belongs to the request now, and its PaymentIntent to the payment.
        var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);
        if (hold.ConsumedAt is not null || hold.ExpiresAt > now)
            return false;

        if (hold.StripePaymentIntentId is { } intent)
        {
            if (funds is null)
            {
                logger.LogWarning("Showcase hold {HoldId} has a card block and cannot release it: kept", hold.Id);
                return false;
            }

            try
            {
                await funds.CancelHoldAuthorizationAsync(hold.Id, intent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kept: the customer's card must not stay blocked, so the next run tries again.
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Showcase hold {HoldId}: its card block could not be released now; the next run tries again", hold.Id);
                return false;
            }
        }

        try
        {
            db.ShowcaseBookingHolds.Remove(hold);
            await db.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Showcase hold {HoldId} could not be deleted after its block was released: the next run deletes it", hold.Id);
            return false;
        }
    }

    /// <summary>The request and the customer a hold became.</summary>
    private sealed record BornRequest(ServiceRequest Request, ServiceCustomer Customer);

    /// <summary>
    /// Turns a hold into the customer, the request and — for a booking whose card is blocked — the <c>Authorized</c> payment, and consumes the
    /// hold, all in the changes of the caller's transaction, which holds the supplier's calendar lock and saves. <b>The one place a request is
    /// born from a hold</b>: the check of the e-mail and the webhook of the block both come here, whichever is last.
    /// </summary>
    private async Task<BornRequest> CreateRequestFromHoldAsync(
        Guid orgId,
        ShowcaseBookingHold hold,
        ShowcaseBookingPayload payload,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var settings = await agenda.GetBookingSettingsAsync(orgId, cancellationToken);
        var customer = await UpsertCustomerAsync(orgId, hold, payload, now, cancellationToken);

        // Decision D8: the supplier answers within SupplierSettings.RespondWithinMinutes (180 by default), or the request lapses
        // (the always-on service-request-expiry job). An urgent request lapses at the latest when the work was due to start: an
        // answer after that hour would be of no use to the customer, who must be able to look elsewhere.
        var responseDueAt = now.AddMinutes(settings.RespondWithinMinutes);
        if (payload.Urgent && hold.StartUtc < responseDueAt)
            responseDueAt = hold.StartUtc;

        // SP-15c-a: a request whose card is blocked is answered while the block is still usable, however long the supplier's own time is:
        // the customer reads an answer time that is true, and the request closes (the block is released) before the block lapses.
        if (hold.StripePaymentIntentId is not null && funds is not null)
        {
            var latest = funds.LatestResponseAt(hold.PaymentAuthorizedAt ?? now);
            if (latest < responseDueAt)
                responseDueAt = latest;
        }

        var request = new ServiceRequest
        {
            OrgId = orgId,
            SupplierOrgId = orgId,
            RentalContext = ServiceRequestRentalContext.Showcase,
            Source = ServiceRequestSource.Showcase,
            PropertyId = null,
            BookingId = null,
            Category = payload.Category,
            // An urgent slot (SP-04c) is an urgent request: the supplier reads it in `urgency`, and the surcharge is already in
            // the estimate and in the options below.
            Urgency = payload.Urgent ? ServiceRequestUrgency.High : ServiceRequestUrgency.Normal,
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
            ResponseDueAt = responseDueAt,
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

        // The payment is born with the request, with the PaymentIntent that blocks the card, the amount the customer saw and the commission in
        // force now; the request is paid inside CasaZen from its first moment (AddAuthorizedPaymentAsync sets it). A hold that has a block
        // but no funds service to record it cannot become a request: the customer's card would be blocked for a request nobody can charge.
        if (hold.StripePaymentIntentId is { } intent && hold.PaymentAmountCents is { } amount)
        {
            if (funds is null)
                throw new InvalidOperationException("A hold with a card block cannot become a request without the funds service.");

            await funds.AddAuthorizedPaymentAsync(
                request,
                new AuthorizedPaymentInput(
                    intent,
                    amount,
                    hold.PaymentAuthorizedAt ?? now,
                    ServiceCharges.HoldTransferGroup(hold.Id),
                    [new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, payload.ServiceName, amount)]),
                cancellationToken);
        }

        // The hold becomes the request in the same save: it stops counting for the planner as the request starts counting.
        // The payload goes: the data moved to the customer and the request. The token hash stays for the replay.
        hold.ConsumedAt = now;
        hold.ServiceRequestId = request.Id;
        hold.Payload = null;

        return new BornRequest(request, customer);
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

    /// <summary>
    /// The name of the comune the supplier will read: the official name of the ISTAT code when the customer gave a code the list
    /// knows, else the text the customer typed (without a code the name had to match one of the supplier's own comuni to cover the
    /// zone, so it is not free text; a code the list does not know is the same as no code).
    /// </summary>
    private async Task<string> ComuneNameAsync(string? istatCode, string typed, CancellationToken cancellationToken)
    {
        if (istatCode is null)
            return typed;

        var comune = await comuneDirectory.FindByIstatCodeAsync(istatCode, activeOnly: false, cancellationToken);
        return string.IsNullOrWhiteSpace(comune?.Name) ? typed : comune.Name;
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
