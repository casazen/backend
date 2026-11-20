using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICleaningVatService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Whose.</b> A cleaning request of the caller's organization (the org is the predicate: the requests are not tenant-filtered), of a property the scope reaches
/// (<c>InScope</c>, as every host read of the requests), not of the showcase, of category cleaning, the ones the host sees. Anything else is the 404 of a request that does
/// not exist, so another organization's request is never reached.</item>
/// <item><b>The answer.</b> «Sì» stores the standard rate of the country of the cleaning (its property) on the day of the cleaning (<see cref="VatRates"/>); a day the table
/// has no rate for is refused, never answered with a rate of its own. The same answer again changes nothing (who and when stay the first); another answer replaces it, once.</item>
/// <item><b>Ricorda per questa ditta</b> keeps the answer for the company in the organization (<see cref="SupplierVatDefault"/>): every request of the company with no answer
/// of its own reads it. «Non lo so» is never remembered.</item>
/// <item><b>The «Da fare».</b> The answer closes, as the person who answered, the item of the request, the item of its stay raised before FIX/BE-CLEANING-VAT once no other
/// cleaning of the stay waits for its answer, and, with «Ricorda», the items of the other requests of the company the remembered answer now covers.</item>
/// <item><b>Logs</b> carry ids and the answer: never a name or an amount.</item>
/// </list>
/// </remarks>
public sealed class CleaningVatService(
    AppDbContext db,
    IInternalActivityService activities,
    TimeProvider time,
    ILogger<CleaningVatService> logger) : ICleaningVatService
{
    /// <summary>The states of a cleaning whose invoice the company issued: completed, or paid.</summary>
    private static readonly ServiceRequestStatus[] Done = [ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato];

    public async Task<CleaningVatQuestion> GetAsync(HostScope scope, Guid requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var request = await RequestsOf(scope).AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw NotFound(requestId);
        return await QuestionAsync(scope.OrgId, request, ct);
    }

    public async Task<CleaningVatQuestion> AnswerAsync(
        HostScope scope,
        Guid requestId,
        CleaningSupplierVat? answer,
        bool remember,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (answer is not { } given || !Enum.IsDefined(given))
            throw new DomainRuleException(CleaningVatErrors.AnswerInvalid, CleaningVatErrors.AnswerInvalidMessageKey);
        if (remember && given == CleaningSupplierVat.Unknown)
            throw new DomainRuleException(CleaningVatErrors.RememberUnknown, CleaningVatErrors.RememberUnknownMessageKey);

        var request = await RequestsOf(scope).FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw NotFound(requestId);
        if (!Done.Contains(request.Status))
            throw new DomainRuleException(CleaningVatErrors.NotCompleted, CleaningVatErrors.NotCompletedMessageKey);

        var (country, _) = await PropertyOfAsync(scope.OrgId, request.PropertyId!.Value, ct);
        decimal? rate = null;
        if (given == CleaningSupplierVat.Applied)
        {
            var day = CleaningSupplierVatReader.DayOf(request.CompletedAt, request.PaidAt, request.UpdatedAt);
            rate = VatRates.Find(country, VatRateKind.Standard, day)?.Percent
                ?? throw new DomainRuleException(CleaningVatErrors.RateUnknown, CleaningVatErrors.RateUnknownMessageKey);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var by = userId.Length <= TodoLimits.UserIdMaxLength ? userId : userId[..TodoLimits.UserIdMaxLength];
        var replaced = request.SupplierVat != given;
        if (replaced)
        {
            request.SupplierVat = given;
            request.SupplierVatRatePercent = rate;
            request.SupplierVatAnsweredAt = now;
            request.SupplierVatAnsweredByUserId = by;
        }

        var remembered = remember && await RememberAsync(scope.OrgId, request.SupplierOrgId, given == CleaningSupplierVat.Applied, by, now, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another operation (the company, another tab) changed the request since it was read: nothing was saved.
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, SupplierVatDefault.OrgSupplierIndexName))
        {
            // Another call remembered an answer for the same company first: nothing of this one was saved, the caller reads again and answers.
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }

        await CloseTodosAsync(scope.OrgId, request, remember ? request.SupplierOrgId : null, by, ct);
        logger.LogInformation(
            "Cleaning request {RequestId} of org {OrgId}: the VAT of the company answered {Answer} by {UserId} ({Outcome}, company remembered: {Remembered})",
            request.Id, scope.OrgId, given, by, replaced ? "stored" : "unchanged", remembered);
        return await QuestionAsync(scope.OrgId, request, ct);
    }

    /// <summary>
    /// The cleaning requests the scope reaches: the org named (no tenant filter on the requests), a property the scope reaches, not of the showcase, of category
    /// cleaning, the ones the host sees.
    /// </summary>
    private IQueryable<ServiceRequest> RequestsOf(HostScope scope) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.OrgId == scope.OrgId
                        && r.RentalContext != ServiceRequestRentalContext.Showcase
                        && r.PropertyId != null
                        && r.Category == ServiceCategories.Cleaning)
            .InScope(scope)
            .OnlyVisibleToHost();

    /// <summary>Remembers the answer for the company in the organization; <c>false</c> when it was already remembered so (nothing written).</summary>
    private async Task<bool> RememberAsync(Guid orgId, Guid companyId, bool appliesVat, string userId, DateTime now, CancellationToken ct)
    {
        var row = await db.SupplierVatDefaults.FirstOrDefaultAsync(d => d.OrgId == orgId && d.SupplierOrgId == companyId, ct);
        if (row is null)
        {
            row = new SupplierVatDefault { OrgId = orgId, SupplierOrgId = companyId };
            db.SupplierVatDefaults.Add(row);
        }
        else if (row.AppliesVat == appliesVat)
        {
            return false;
        }

        row.AppliesVat = appliesVat;
        row.UpdatedAt = now;
        row.UpdatedByUserId = userId;
        return true;
    }

    /// <summary>
    /// Closes, as <paramref name="userId"/>, the «Da fare» the answer settles: the one of the request; the one of its stay raised before FIX/BE-CLEANING-VAT, once every
    /// cleaning of the stay has its VAT stated; with <paramref name="company"/>, the ones of the other requests of the company that the remembered answer now states.
    /// </summary>
    private async Task CloseTodosAsync(Guid orgId, ServiceRequest request, Guid? company, string userId, CancellationToken ct)
    {
        var type = nameof(ActivityType.CleaningVatToState);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && a.Action != null && a.CompletedAt == null && a.ActivityType == type && a.EntityId != null)
            .Select(a => new { a.Id, a.EntityType, EntityId = a.EntityId!.Value })
            .ToListAsync(ct);
        if (open.Count == 0)
            return;

        var close = open.Where(a => a.EntityType == CleaningVatRules.TodoEntityType && a.EntityId == request.Id).Select(a => a.Id).ToList();

        if (request.BookingId is { } stay && open.Any(a => a.EntityType == InternalActivity.BookingEntityType && a.EntityId == stay))
        {
            var cleaningsOfStay = await db.ServiceRequests
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && r.BookingId == stay && r.Category == ServiceCategories.Cleaning && Done.Contains(r.Status))
                .Select(r => r.Id)
                .ToListAsync(ct);
            var stated = await CleaningSupplierVatReader.ReadAsync(db, orgId, cleaningsOfStay, ct);
            if (cleaningsOfStay.All(stated.ContainsKey))
                close.AddRange(open.Where(a => a.EntityType == InternalActivity.BookingEntityType && a.EntityId == stay).Select(a => a.Id));
        }

        if (company is { } companyId)
        {
            var asked = open.Where(a => a.EntityType == CleaningVatRules.TodoEntityType && a.EntityId != request.Id).Select(a => a.EntityId).ToList();
            var ofCompany = asked.Count == 0
                ? []
                : await db.ServiceRequests
                    .AsNoTracking()
                    .Where(r => r.OrgId == orgId && r.SupplierOrgId == companyId && asked.Contains(r.Id))
                    .Select(r => r.Id)
                    .ToListAsync(ct);
            var stated = await CleaningSupplierVatReader.ReadAsync(db, orgId, ofCompany, ct);
            close.AddRange(open.Where(a => a.EntityType == CleaningVatRules.TodoEntityType && stated.ContainsKey(a.EntityId)).Select(a => a.Id));
        }

        foreach (var id in close.Distinct())
            await activities.CloseAsync(id, userId, ct);
    }

    private async Task<CleaningVatQuestion> QuestionAsync(Guid orgId, ServiceRequest request, CancellationToken ct)
    {
        var propertyId = request.PropertyId!.Value;
        var (country, propertyOwner) = await PropertyOfAsync(orgId, propertyId, ct);
        var day = CleaningSupplierVatReader.DayOf(request.CompletedAt, request.PaidAt, request.UpdatedAt);
        var company = await db.SupplierVatDefaults
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.SupplierOrgId == request.SupplierOrgId)
            .Select(d => (bool?)d.AppliesVat)
            .FirstOrDefaultAsync(ct);
        var name = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == request.SupplierOrgId)
            .Select(o => o.DisplayName ?? o.Name)
            .FirstOrDefaultAsync(ct);
        var stated = await CleaningSupplierVatReader.ReadAsync(db, orgId, [request.Id], ct);

        return new CleaningVatQuestion(
            request.Id,
            propertyId,
            propertyOwner,
            request.RentalContext,
            request.BookingId,
            request.SupplierOrgId,
            name,
            country,
            day,
            VatRates.Find(country, VatRateKind.Standard, day)?.Percent,
            request.SupplierVat,
            request.SupplierVatRatePercent,
            request.SupplierVatAnsweredAt,
            company,
            stated.GetValueOrDefault(request.Id));
    }

    /// <summary>The country of the property of the cleaning (deleted ones too: the cleaning stays the owner's) and the user who created it.</summary>
    private async Task<(string Country, string? OwnerUserId)> PropertyOfAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var property = await db.Properties
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .Select(p => new { p.Country, p.OwnerId })
            .FirstOrDefaultAsync(ct);
        return ((property?.Country ?? string.Empty).Trim().ToUpperInvariant(), property?.OwnerId);
    }

    private static NotFoundException NotFound(Guid requestId) =>
        new($"Cleaning request {requestId} was not found")
        {
            Code = ServiceRequestErrorCodes.NotFound,
            MessageKey = ServiceRequestErrorCodes.NotFoundMessageKey,
        };
}
