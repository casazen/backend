using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertyCleaningSetupService" />
/// <remarks>
/// <para><b>Whose.</b> Every statement names the org, and a property that is not one of it (or is deleted, or is outside the scope of a bulk request) is
/// <c>property_not_found</c>, the answer of a property that does not exist: the service never tells that it exists. The web layer decides who may call (the policy and the
/// host resource of the property); the scope of a bulk request is applied here, in the query (<c>InScope</c>), never by a list filtered afterwards.</para>
/// <para><b>The checks of the supplier</b> are the ones a service request makes when it is created (<c>ServiceRequestService.CreateAsync</c>): a supplier profile, active, that
/// covers the comune of the property (by its ISTAT code, else by the written name). <b>The checks of the staff member</b> are the ones that let a person be put in charge of a
/// property (<see cref="PropertyStaffReach"/>). Both are made again when the cleaning is created, because the supplier can be suspended and the member deactivated later.</para>
/// <para><b>The price</b> is a number of cents and never a currency of its own: it is the price in the currency of the property, and a request that states another currency is
/// refused. Nothing is converted. For the same reason a property that is not in euro takes no supplier (the requests to the suppliers, their prices and their payments are in euro):
/// only a member of the staff cleans it.</para>
/// </remarks>
public sealed class PropertyCleaningSetupService(
    AppDbContext db,
    ISupplierComuneMatcher comuneMatcher,
    ILogger<PropertyCleaningSetupService> logger,
    TimeProvider? timeProvider = null) : IPropertyCleaningSetupService
{
    private const string PropertyNotFoundCode = "property_not_found";
    private const string PropertyNotFoundMessageKey = "PropertyNotFound";
    private const string LongModeCode = "property_not_bookable_in_long_mode";
    private const string LongModeMessageKey = "PropertyNotBookableInLongMode";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<PropertyCleaningSetupView> GetAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var property = await FindPropertyAsync(orgId, propertyId, cancellationToken);
        var setup = await db.PropertyCleaningSetups
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, cancellationToken);

        return await ViewAsync(property, setup, cancellationToken);
    }

    public async Task<PropertyCleaningSetupView> SaveAsync(
        Guid orgId,
        Guid propertyId,
        CleaningSetupInput input,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var property = await FindPropertyAsync(orgId, propertyId, cancellationToken);
        ValidateShape(input);

        var setup = await UpsertAsync(property, input, actorUserId, cancellationToken);
        return await ViewAsync(property, setup, cancellationToken);
    }

    public async Task<IReadOnlyList<CleaningSetupBulkItem>> SaveBulkAsync(
        Guid orgId,
        HostScope scope,
        IReadOnlyCollection<Guid> propertyIds,
        CleaningSetupInput input,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(propertyIds);
        ArgumentNullException.ThrowIfNull(input);
        if (scope.OrgId != orgId)
            throw new ArgumentException("The scope of a bulk save is the scope of the org it saves for.", nameof(scope));

        // None is none: an empty list never means every property of the org.
        var ids = propertyIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainRuleException(PropertyCleaningSetupRules.BulkEmpty, "CleaningSetupBulkEmpty");
        if (ids.Count > PropertyCleaningSetupRules.MaxBulkProperties)
            throw new DomainRuleException(PropertyCleaningSetupRules.BulkTooMany, "CleaningSetupBulkTooMany", PropertyCleaningSetupRules.MaxBulkProperties);

        // What is wrong with the setup itself is wrong for every property: nothing is saved.
        ValidateShape(input);

        // Only the properties of the org inside the reach of the caller (a collaborator «Solo alcuni» reaches the ones it was given): the others are
        // the same "not found" as a property that does not exist.
        var reachable = (await db.Properties
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
                .InScope(scope)
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);

        var results = new List<CleaningSetupBulkItem>(ids.Count);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reachable.TryGetValue(id, out var property))
            {
                results.Add(Failed(id, PropertyNotFoundCode, PropertyNotFoundMessageKey, []));
                continue;
            }

            try
            {
                await UpsertAsync(property, input, actorUserId, cancellationToken);
                results.Add(new CleaningSetupBulkItem(id, Saved: true, Code: null, MessageKey: null, MessageArgs: []));
            }
            catch (DomainException ex)
            {
                results.Add(Failed(id, ex.Code, ex.MessageKey, ex.MessageArgs));
            }
            catch (NotFoundException ex)
            {
                results.Add(Failed(id, ex.Code ?? PropertyNotFoundCode, ex.MessageKey ?? PropertyNotFoundMessageKey, []));
            }
        }

        logger.LogInformation(
            "Cleaning setup of {Total} properties of org {OrgId} saved in bulk by {UserId}: {Saved} saved, {Failed} not",
            results.Count, orgId, actorUserId, results.Count(r => r.Saved), results.Count(r => !r.Saved));
        return results;
    }

    /// <summary>The property of the org (not deleted), as it is read to validate and to answer; the same 404 for another org's property and a missing one.</summary>
    private async Task<Property> FindPropertyAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken) =>
        await db.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken)
        ?? throw new NotFoundException($"Property {propertyId} not found")
        {
            Code = PropertyNotFoundCode,
            MessageKey = PropertyNotFoundMessageKey,
        };

    /// <summary>
    /// What is wrong with the setup whatever the property: the kind of cleaner and the columns it needs, the price, the time. 422 with the codes of
    /// <see cref="PropertyCleaningSetupRules"/>; nothing is read from the database.
    /// </summary>
    private static void ValidateShape(CleaningSetupInput input)
    {
        if (input.CleanerKind is not { } kind || !Enum.IsDefined(kind))
            throw new DomainRuleException(PropertyCleaningSetupRules.CleanerRequired, "CleaningSetupCleanerRequired");

        var staffUserId = input.StaffUserId?.Trim();
        if (kind == PropertyCleanerKind.Supplier)
        {
            if (input.SupplierOrgId is not { } supplier || supplier == Guid.Empty || !string.IsNullOrEmpty(staffUserId))
                throw new DomainRuleException(PropertyCleaningSetupRules.CleanerMismatch, "CleaningSetupCleanerMismatch");

            // A supplier is paid: no price, no cleaning request.
            if (input.PriceCents is null)
                throw new DomainRuleException(PropertyCleaningSetupRules.PriceInvalid, "CleaningSetupPriceInvalid");
        }
        else if (string.IsNullOrEmpty(staffUserId) || staffUserId.Length > PropertyCleaningSetup.UserIdMaxLength || input.SupplierOrgId is not null)
        {
            throw new DomainRuleException(PropertyCleaningSetupRules.CleanerMismatch, "CleaningSetupCleanerMismatch");
        }

        if (input.PriceCents is { } price)
        {
            if (!ServiceRequestPricing.IsValidAmount(price))
                throw new DomainRuleException(PropertyCleaningSetupRules.PriceInvalid, "CleaningSetupPriceInvalid");

            // A number is never applied to a currency nobody stated.
            if (string.IsNullOrWhiteSpace(input.Currency))
                throw new DomainRuleException(PropertyCleaningSetupRules.CurrencyRequired, "CleaningSetupCurrencyRequired");
        }

        if (input.StartTime is { } start && start.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new DomainRuleException(PropertyCleaningSetupRules.StartTimeInvalid, "CleaningSetupStartTimeInvalid");

        if (input.DurationMinutes is { } minutes
            && minutes is < SupplierServiceCatalogLimits.MinDurationMinutes or > SupplierServiceCatalogLimits.MaxDurationMinutes)
        {
            throw new DomainRuleException(
                PropertyCleaningSetupRules.DurationInvalid,
                "CleaningSetupDurationInvalid",
                SupplierServiceCatalogLimits.MinDurationMinutes,
                SupplierServiceCatalogLimits.MaxDurationMinutes);
        }
    }

    /// <summary>
    /// What is wrong with the setup for this property: a property that takes no stays, a price in another currency than the property's, and for an active setup a cleaner
    /// that cannot work now. 422 with the codes of <see cref="PropertyCleaningSetupRules"/>.
    /// </summary>
    private async Task ValidateForPropertyAsync(Property property, CleaningSetupInput input, CancellationToken cancellationToken)
    {
        // The cleaning is the one of a stay at check-out: a long-rent property has none.
        if (property.RentalMode == RentalMode.Long)
            throw new DomainRuleException(LongModeCode, LongModeMessageKey);

        var currency = StayCurrency.ForProperty(property);
        if (input.PriceCents is not null && !string.Equals(StayCurrency.Normalize(input.Currency), currency, StringComparison.Ordinal))
            throw new DomainRuleException(PropertyCleaningSetupRules.CurrencyMismatch, "CleaningSetupCurrencyMismatch", currency);

        // The requests to the suppliers of the marketplace, their prices and their payments are in euro (ServiceCharges.Currency) and nothing is converted: a price in francs would be charged
        // as euro. A property in another currency takes no supplier, only a member of the staff. An inactive setup is only kept, like for every other check of the cleaner.
        if (input.CleanerKind == PropertyCleanerKind.Supplier && input.Active && !string.Equals(currency, StayCurrency.Euro, StringComparison.Ordinal))
            throw new DomainRuleException(PropertyCleaningSetupRules.SupplierCurrencyUnsupported, "CleaningSetupSupplierCurrencyUnsupported", currency);

        if (input.CleanerKind == PropertyCleanerKind.Supplier)
        {
            var supplierOrgId = input.SupplierOrgId!.Value;

            // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
            var supplier = await SupplierServiceCatalogService.SuppliersForProperty(db, property.OrgId, property.Id)
                               .AsNoTracking()
                               .FirstOrDefaultAsync(sp => sp.OrgId == supplierOrgId, cancellationToken)
                           ?? throw new DomainRuleException(PropertyCleaningSetupRules.SupplierNotFound, "CleaningSetupSupplierNotFound");

            // An inactive setup is only kept: a supplier suspended since never stops the host from switching the automation off.
            if (!input.Active)
                return;

            if (supplier.Status != SupplierStatus.Active)
                throw new DomainRuleException(PropertyCleaningSetupRules.SupplierInactive, "CleaningSetupSupplierInactive");

            // By ISTAT code when the property has a chosen comune (SU-04), by the written city otherwise: the same check as a service request.
            if (!await comuneMatcher.CoversAsync(supplier, ComuneTarget.ForProperty(property), cancellationToken))
                throw new DomainRuleException(PropertyCleaningSetupRules.SupplierOutsideComune, "CleaningSetupSupplierOutsideComune");

            return;
        }

        if (input.Active
            && !await PropertyStaffReach.CanBeGivenWorkAsync(
                db, property.OrgId, property.Id, property.RentalMode, input.StaffUserId!.Trim(), cancellationToken))
        {
            throw new DomainRuleException(PropertyCleaningSetupRules.StaffInvalid, "CleaningSetupStaffInvalid");
        }
    }

    /// <summary>
    /// Validates the setup for the property and replaces what the property had (or creates the row). Two parallel first saves lose to each other on the unique index of the
    /// property: the loser starts again and finds the row of the winner.
    /// </summary>
    private async Task<PropertyCleaningSetup> UpsertAsync(
        Property property,
        CleaningSetupInput input,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        await ValidateForPropertyAsync(property, input, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        for (var attempt = 0; ; attempt++)
        {
            var setup = await db.PropertyCleaningSetups
                .FirstOrDefaultAsync(s => s.OrgId == property.OrgId && s.PropertyId == property.Id, cancellationToken);
            var isNew = setup is null;
            setup ??= new PropertyCleaningSetup { OrgId = property.OrgId, PropertyId = property.Id, CreatedAt = now };

            var kind = input.CleanerKind!.Value;
            setup.CleanerKind = kind;
            setup.SupplierOrgId = kind == PropertyCleanerKind.Supplier ? input.SupplierOrgId : null;
            setup.StaffUserId = kind == PropertyCleanerKind.Staff ? input.StaffUserId!.Trim() : null;
            setup.PriceCents = input.PriceCents;
            setup.StartTime = input.StartTime;
            setup.DurationMinutes = input.DurationMinutes;
            setup.Active = input.Active;
            setup.UpdatedAt = now;
            setup.UpdatedByUserId = Truncate(actorUserId);
            if (isNew)
                db.PropertyCleaningSetups.Add(setup);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (isNew && attempt == 0 && OrgTeamAccess.IsUniqueViolation(ex, PropertyCleaningSetup.PropertyIndexName))
            {
                db.Entry(setup).State = EntityState.Detached;
                continue;
            }

            logger.LogInformation(
                "Cleaning setup of property {PropertyId} of org {OrgId} saved by {UserId}: {Kind}, active {Active}",
                property.Id, property.OrgId, actorUserId, kind, setup.Active);
            return setup;
        }
    }

    private async Task<PropertyCleaningSetupView> ViewAsync(Property property, PropertyCleaningSetup? setup, CancellationToken cancellationToken)
    {
        var currency = StayCurrency.ForProperty(property);
        var defaultStart = CleaningSchedule.StartTimeOf(null, property.CheckOutBy);
        if (setup is null)
        {
            return new PropertyCleaningSetupView(
                property.Id,
                Configured: false,
                CleanerKind: null,
                SupplierOrgId: null,
                SupplierName: null,
                SupplierIsTrusted: false,
                StaffUserId: null,
                PriceCents: null,
                currency,
                StartTime: null,
                defaultStart,
                DurationMinutes: null,
                CleaningSchedule.DefaultDurationMinutes,
                Active: false,
                UpdatedAt: null);
        }

        string? supplierName = null;
        var trusted = false;
        if (setup.SupplierOrgId is { } supplierOrgId)
        {
            // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
            supplierName = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == supplierOrgId)
                .Select(sp => sp.LegalName)
                .FirstOrDefaultAsync(cancellationToken);

            // A trusted supplier of the org, for this property: the ones it was assigned (none listed = every property of the org).
            var links = await db.TrustedSuppliers
                .AsNoTracking()
                .Where(t => t.OrgId == setup.OrgId && t.SupplierOrgId == supplierOrgId)
                .Select(t => t.AssignedPropertyIds)
                .ToListAsync(cancellationToken);
            trusted = links.Any(assigned => assigned.Count == 0 || assigned.Contains(property.Id));
        }

        return new PropertyCleaningSetupView(
            property.Id,
            Configured: true,
            setup.CleanerKind,
            setup.SupplierOrgId,
            supplierName,
            trusted,
            setup.StaffUserId,
            setup.PriceCents,
            currency,
            setup.StartTime,
            CleaningSchedule.StartTimeOf(setup.StartTime, property.CheckOutBy),
            setup.DurationMinutes,
            CleaningSchedule.DurationOf(setup.DurationMinutes),
            setup.Active,
            setup.UpdatedAt);
    }

    private static CleaningSetupBulkItem Failed(Guid propertyId, string code, string messageKey, IReadOnlyList<object> messageArgs) =>
        new(propertyId, Saved: false, code, messageKey, messageArgs);

    private static string? Truncate(string? userId) =>
        userId is null ? null : userId.Length <= PropertyCleaningSetup.UserIdMaxLength ? userId : userId[..PropertyCleaningSetup.UserIdMaxLength];
}
