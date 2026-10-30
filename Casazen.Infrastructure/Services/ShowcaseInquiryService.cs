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

/// <inheritdoc cref="IShowcaseInquiryService"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ShowcaseInquiries</c> and <c>ServiceRequestMessages</c> are keyed by the supplier org and not tenant-filtered
/// (the TN-2 allow-list says why: the customer is anonymous and a supplier-only account has no <c>User.OrgId</c>). Every statement on
/// them goes through <see cref="InquiriesOf"/> or is an insert/remove of a row that has its <c>OrgId</c> set;
/// <c>ShowcaseInquiryTenancyTests</c> keeps it that way. The customers and the booking holds are read through the helpers of
/// <see cref="ShowcaseBookingService"/> (<c>CustomerByEmailOf</c>, <c>HoldsOf</c>), which carry the same predicate.</para>
/// <para><b>Concurrency.</b> Both operations run in a READ COMMITTED transaction that takes the supplier's calendar lock
/// (<c>SupplierCalendarSync</c>, the lock under which every showcase request and hold of the supplier is created), so what the previous
/// holder committed is visible: a replay is recognized, the cap of the requests that wait is counted on the real number and the public
/// code is free when it is written. There is no slot to judge: the lock is only there for the numbers. The check of the e-mail takes the
/// same lock, so the inquiry stops waiting and the request starts existing inside one transaction. Outside PostgreSQL (EF InMemory in
/// unit tests) nothing is locked.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never a name, an address, an e-mail, a token or a word of the customer.</para>
/// </remarks>
public sealed class ShowcaseInquiryService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierComuneMatcher comuneMatcher,
    IComuneDirectory comuneDirectory,
    IServiceCustomerIndex customerIndex,
    ShowcaseBookingNotifier notifier,
    IOptions<ShowcaseBookingOptions> options,
    ILogger<ShowcaseInquiryService> logger,
    TimeProvider? timeProvider = null) : IShowcaseInquiryService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseInquiryResult> CreateAsync(
        SupplierProfile supplier,
        ShowcaseInquiryInput input,
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
            logger.LogError("Suppliers:Showcase:PrivacyNoticeVersion is not configured: no request can record its consent");
            throw ShowcaseBookingErrors.OldConsent();
        }

        var content = ShowcaseInquiryRules.Normalize(input, privacyVersion);
        var orgId = supplier.OrgId;

        var service = content.ServiceSlug is { } slug ? await FindServiceAsync(orgId, slug, cancellationToken) : null;

        if (!await comuneMatcher.CoversAsync(supplier, new ComuneTarget(content.ComuneIstat, content.City), cancellationToken))
            throw ShowcaseBookingErrors.OutsideSupplierZone();

        // With the code of the comune the supplier reads the official name, not the text typed next to it.
        var city = await ComuneNameAsync(content.ComuneIstat, content.City, cancellationToken);

        var emailHash = customerIndex.HashEmail(content.Email);
        var token = ShowcaseBookingTokens.New();
        var inquiryId = Guid.NewGuid();
        var validMinutes = config.EmailVerificationMinutes;

        var payload = new ShowcaseInquiryPayload(
            service?.ListingId,
            service?.Service.Name,
            service?.Service.Category ?? CategoryOf(supplier),
            content.FullName,
            content.Email,
            content.Phone,
            content.Locale,
            content.ComuneIstat,
            city,
            content.Message,
            content.PrivacyNoticeVersion,
            DateTime.MinValue,
            content.ConsentIp);

        // Rendered before the row is saved: a missing App:PublicSiteBaseUrl is a configuration error that has to stop the request,
        // not a wrong link in an e-mail.
        var verification = notifier.RenderInquiryVerification(supplier, payload, inquiryId, token, validMinutes);

        ShowcaseInquiryResult result;
        ShowcaseInquiry created;
        await using (var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken))
        {
            // Everything from here on is read after the lock: what the previous holder committed is there.
            var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

            var existing = await InquiriesOf(db, orgId)
                .Where(i => i.ClientRequestId == content.ClientRequestId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                // The same client request id is the same inquiry: a retry after a lost answer sends no second e-mail. A row that lapsed
                // (and is not yet deleted by the upkeep job) is out of the way instead: the unique index would refuse the new one.
                if (existing.ExpiresAt > now || existing.ConsumedAt is not null)
                {
                    logger.LogInformation("Showcase inquiry {InquiryId} of supplier {OrgId}: replay of the same client request", existing.Id, orgId);
                    return new ShowcaseInquiryResult(existing.Id, existing.ExpiresAt);
                }

                db.ShowcaseInquiries.Remove(existing);
                await SaveAsync(cancellationToken);
            }

            // At most three requests of one address wait at the same time, for one supplier: the ones whose e-mail is not checked yet and
            // the ones the supplier did not answer. The fourth is a 429 like the limits per IP and per address, so the three look alike.
            var waiting = await InquiriesOf(db, orgId)
                .CountAsync(i => i.EmailHash == emailHash && i.ConsumedAt == null && i.ExpiresAt > now, cancellationToken)
                + await OpenCustomRequestsOf(db, orgId, emailHash).CountAsync(cancellationToken);
            if (waiting >= ShowcaseInquiryLimits.MaxWaitingPerEmail)
            {
                logger.LogInformation("Showcase inquiry of supplier {OrgId} refused: the address already has the most it may have waiting", orgId);
                throw new ShowcaseBookingTooManyHoldsException(TimeSpan.FromMinutes(validMinutes));
            }

            payload = payload with { PrivacyAcceptedAt = now };

            var inquiry = new ShowcaseInquiry
            {
                Id = inquiryId,
                OrgId = orgId,
                ClientRequestId = content.ClientRequestId,
                TokenHash = ShowcaseBookingTokens.Hash(token),
                EmailHash = emailHash,
                Payload = payload.ToJson(),
                ExpiresAt = now.AddMinutes(validMinutes),
                CreatedAt = now,
            };
            db.ShowcaseInquiries.Add(inquiry);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            created = inquiry;
            result = new ShowcaseInquiryResult(inquiry.Id, inquiry.ExpiresAt);
        }

        logger.LogInformation("Showcase inquiry {InquiryId} of supplier {OrgId} created", result.Id, orgId);

        // After the commit: an e-mail for an inquiry that was not saved would be a link to nothing.
        if (!notifier.QueueInquiryVerification(content.Email, verification))
        {
            // The customer cannot be written to (provider not configured, queue down): nobody can check the address, and the customer
            // would wait for an e-mail that never comes. Nobody has the token yet, so nothing can be checking it: the row goes, and the
            // customer can try again.
            await ReleaseAsync(created, cancellationToken);
            logger.LogError("Showcase inquiry {InquiryId} of supplier {OrgId} released: the verification e-mail could not be queued", result.Id, orgId);
            throw new EmailConfigurationException("The verification e-mail of a showcase inquiry could not be queued.");
        }

        return result;
    }

    public async Task<ShowcaseInquiryConfirmation> ConfirmEmailAsync(
        SupplierProfile supplier,
        Guid inquiryId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        var orgId = supplier.OrgId;

        ServiceRequest request;
        ServiceCustomer customer;
        ShowcaseInquiryConfirmation result;
        await using (var transaction = await LockSupplierCalendarAsync(orgId, cancellationToken))
        {
            var inquiry = await InquiriesOf(db, orgId).Where(i => i.Id == inquiryId).FirstOrDefaultAsync(cancellationToken);

            // One answer for an inquiry that does not exist, one of another supplier and a wrong token.
            if (inquiry is null || !ShowcaseBookingTokens.Matches(inquiry.TokenHash, token))
                throw ShowcaseBookingErrors.InvalidLink();

            var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

            // A second click on the link (or a retry) answers the same, with the request as it is by then. Nothing happens again.
            if (inquiry.ConsumedAt is not null)
                return await ReplayAsync(inquiry, orgId, cancellationToken);

            if (inquiry.ExpiresAt <= now)
                throw ShowcaseBookingErrors.ExpiredLink();

            // A supplier suspended meanwhile takes no new request (the profile is read again under the lock).
            var status = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == orgId)
                .Select(sp => (SupplierStatus?)sp.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (status != SupplierStatus.Active)
                throw ShowcaseBookingErrors.InactiveSupplier();

            var payload = ShowcaseInquiryPayload.FromJson(inquiry.Payload);
            if (payload is null)
            {
                logger.LogError("Showcase inquiry {InquiryId} of supplier {OrgId} has no readable payload", inquiry.Id, orgId);
                throw ShowcaseBookingErrors.InvalidLink();
            }

            customer = await UpsertCustomerAsync(orgId, inquiry, payload, now, cancellationToken);
            var publicCode = await NewPublicCodeAsync(orgId, cancellationToken);

            request = new ServiceRequest
            {
                OrgId = orgId,
                SupplierOrgId = orgId,
                RentalContext = ServiceRequestRentalContext.Showcase,
                Source = ServiceRequestSource.Showcase,
                Kind = ServiceRequestKind.Custom,
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
                // No time and no price: they come with the offer of the supplier. The supplier has the time of the configuration to
                // answer, or the request lapses (the always-on service-request-expiry job); its first message stops the clock.
                ResponseDueAt = now.AddMinutes(options.Value.InquiryResponseMinutes),
                PublicCode = publicCode,
                CustomerId = customer.Id,
                LocationComuneIstat = payload.ComuneIstat,
                LocationCity = payload.City,
            };
            db.ServiceRequests.Add(request);

            // What the customer wrote is the first message of the thread, encrypted by the column.
            db.ServiceRequestMessages.Add(new ServiceRequestMessage
            {
                ServiceRequestId = request.Id,
                OrgId = orgId,
                Author = ServiceRequestMessageAuthor.Customer,
                Body = payload.Message,
                CreatedAt = now,
            });

            // The inquiry becomes the request in the same save. The payload goes: the data moved to the customer, the request and the
            // message. The token hash stays for the replay.
            inquiry.ConsumedAt = now;
            inquiry.ServiceRequestId = request.Id;
            inquiry.Payload = null;

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);

            result = new ShowcaseInquiryConfirmation(
                request.PublicCode!,
                request.Status,
                request.ServiceNameSnapshot,
                request.ResponseDueAt,
                AlreadyConfirmed: false);
        }

        logger.LogInformation("Showcase request on measure {RequestId} of supplier {OrgId} created from inquiry {InquiryId}", request.Id, orgId, inquiryId);

        await notifier.QueueInquiryReceivedAsync(supplier, request, customer, request.ResponseDueAt!.Value, cancellationToken);
        return result;
    }

    /// <summary>
    /// The inquiries of <paramref name="orgId"/>. <b>The only way</b> this service reads or changes an inquiry: the table is not
    /// tenant-filtered, so the supplier org is always an explicit predicate. Static and internal so a test can read the SQL it becomes
    /// on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<ShowcaseInquiry> InquiriesOf(AppDbContext db, Guid orgId) =>
        db.ShowcaseInquiries.Where(i => i.OrgId == orgId);

    /// <summary>
    /// The requests on measure of <paramref name="orgId"/> that wait for the supplier (<c>Richiesto</c>) and belong to the customer with the
    /// e-mail index <paramref name="emailHash"/>. Static and internal like <see cref="InquiriesOf"/>.
    /// </summary>
    internal static IQueryable<ServiceRequest> OpenCustomRequestsOf(AppDbContext db, Guid orgId, string emailHash)
    {
        var customerIds = ShowcaseBookingService.CustomerByEmailOf(db, orgId, emailHash).Select(c => (Guid?)c.Id);

        // ServiceRequest is not tenant-filtered (two parties, TN-2 allow-list); scoped by the explicit supplier org.
        return db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.SupplierOrgId == orgId
                        && r.Kind == ServiceRequestKind.Custom
                        && r.Status == ServiceRequestStatus.Richiesto
                        && customerIds.Contains(r.CustomerId));
    }

    /// <summary>Deletes an inquiry nobody can check (see <see cref="CreateAsync"/>); a failure here only leaves it to lapse.</summary>
    private async Task ReleaseAsync(ShowcaseInquiry inquiry, CancellationToken cancellationToken)
    {
        try
        {
            db.ShowcaseInquiries.Remove(inquiry);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Showcase inquiry {InquiryId} could not be released: it lapses by itself", inquiry.Id);
        }
    }

    /// <summary>The service the request is about (id included), or the 404 the public gives for a service that is not published.</summary>
    private async Task<SupplierBookableService> FindServiceAsync(Guid orgId, string slug, CancellationToken cancellationToken)
    {
        var normalized = SupplierShowcaseSlug.Normalize(slug);
        if (!SupplierShowcaseSlug.IsLookupable(normalized, SupplierServiceCatalogLimits.SlugMaxLength))
            throw ServiceNotFound();

        return await catalog.FindBookableAsync(orgId, normalized, cancellationToken) ?? throw ServiceNotFound();
    }

    /// <summary>The category of a request about no service: the first the supplier offers (an active supplier has at least one).</summary>
    private static string CategoryOf(SupplierProfile supplier) =>
        SupplierComuneMatcher.ReadStrings(supplier.CategoriesJson).FirstOrDefault(ServiceCategories.IsKnown) ?? ServiceCategories.Maintenance;

    /// <summary>The official name of the comune of the ISTAT code when the list knows it, else the text the customer typed.</summary>
    private async Task<string> ComuneNameAsync(string? istatCode, string typed, CancellationToken cancellationToken)
    {
        if (istatCode is null)
            return typed;

        var comune = await comuneDirectory.FindByIstatCodeAsync(istatCode, activeOnly: false, cancellationToken);
        return string.IsNullOrWhiteSpace(comune?.Name) ? typed : comune.Name;
    }

    /// <summary>
    /// The customer of this address for this supplier, created or brought up to date with what the customer just wrote (the same rule as
    /// the booking's: one customer per supplier and address, and a request is never attached to somebody else).
    /// </summary>
    private async Task<ServiceCustomer> UpsertCustomerAsync(
        Guid orgId,
        ShowcaseInquiry inquiry,
        ShowcaseInquiryPayload payload,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var customer = await ShowcaseBookingService.CustomerByEmailOf(db, orgId, inquiry.EmailHash).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            customer = new ServiceCustomer
            {
                OrgId = orgId,
                EmailHash = inquiry.EmailHash,
                CreatedAt = now,
            };
            db.ServiceCustomers.Add(customer);
        }
        else if (!ServiceCustomerEmails.SameAddress(customer.Email, payload.Email))
        {
            // The index says it is the same address and the address says it is not: a collision or a key that changed under existing
            // customers. A request must never be attached to somebody else: stop, loudly.
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

    /// <summary>What a second check of the same link answers: the request, as it is now.</summary>
    private async Task<ShowcaseInquiryConfirmation> ReplayAsync(ShowcaseInquiry inquiry, Guid orgId, CancellationToken cancellationToken)
    {
        // ServiceRequest is not tenant-filtered (two parties, TN-2 allow-list); scoped by the explicit supplier org.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == inquiry.ServiceRequestId && r.SupplierOrgId == orgId)
            .Select(r => new { r.PublicCode, r.Status, r.ServiceNameSnapshot, r.ResponseDueAt })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ShowcaseBookingErrors.InvalidLink();

        return new ShowcaseInquiryConfirmation(
            request.PublicCode ?? string.Empty,
            request.Status,
            request.ServiceNameSnapshot,
            request.Status == ServiceRequestStatus.Richiesto ? request.ResponseDueAt : null,
            AlreadyConfirmed: true);
    }

    /// <summary>
    /// A code no hold and no request of the supplier has. 50 random bits: the first try is enough; the check is the guarantee, under the
    /// lock that every hold and every request of the supplier is created under.
    /// </summary>
    private async Task<string> NewPublicCodeAsync(Guid orgId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ShowcaseBookingLimits.PublicCodeAttempts; attempt++)
        {
            var code = BookingCodes.New();
            var taken = await ShowcaseBookingService.HoldsOf(db, orgId).AnyAsync(h => h.PublicCode == code, cancellationToken)
                        || await db.ServiceRequests.IgnoreQueryFilters()
                            .AnyAsync(r => r.SupplierOrgId == orgId && r.PublicCode == code, cancellationToken);
            if (!taken)
                return code;
        }

        throw new InvalidOperationException("No free booking code found.");
    }

    /// <summary>
    /// Saves, turning a lost race into a 409: the unique index of an inquiry or of a code, or a state that changed under a request. Under
    /// the supplier's calendar lock neither should happen; this is the guarantee that it is never a 500 if it does.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StateChanged();
        }
        catch (DbUpdateException ex) when (IsConflict(ex))
        {
            logger.LogInformation("A showcase inquiry lost a race on the database: {SqlState}", PostgresError(ex)?.SqlState);
            throw StateChanged();
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

    private static DomainConflictException StateChanged() =>
        new(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);

    /// <summary>The generic code of a 404 (the web layer's <c>ProblemCodes.NotFound</c>, which Infrastructure cannot name).</summary>
    private const string ProblemNotFound = "not_found";
}
