using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

public sealed record PreparationSelection(Guid OrgId, Guid PropertyId);
public sealed record PreparationCandidate(Guid OrgId, Guid PropertyId, string OrganizationName, string PropertyName, IReadOnlyList<string> MissingFields);
public sealed record PreparationCandidates(IReadOnlyList<PreparationCandidate> Items, bool HasMore);
public sealed record PreparationRow(Guid Id, Guid BatchId, Guid OrgId, Guid PropertyId, string PropertyName,
    string Status, string? AccommodationId, string? SupplierReference, string? ErrorCode, DateTime UpdatedAt);
public sealed record PreparationBatch(Guid Id, IReadOnlyList<PreparationRow> Rows);

public sealed class OctoratePreparationService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope scope,
    OctoratePropertyCreator creator,
    IOptions<OctorateOptions> options,
    IHostEnvironment host,
    TimeProvider clock)
{
    private const string Tenant = AppDbContext.TenantQueryFilter;
    private const string EnvironmentCheckFailed = "octorate_preparation_environment_mismatch";
    private const string MasterCheckFailed = "octorate_preparation_master_check_failed";
    private readonly OctorateEnvironment _environment = OctorateEnvironments.Of(host);
    private OctorateEnvironment ExpectedApiEnvironment =>
        OctorateEnvironments.ExpectedApiEnvironment(_environment, options.Value.AllowProductionApiInTest);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private IQueryable<OctoratePreparation> Rows => db.OctoratePreparations.IgnoreQueryFilters([Tenant])
        .Where(r => r.Environment == _environment);
    private IQueryable<Property> PublishedShortRentProperties => db.Properties.IgnoreQueryFilters([Tenant]).AsNoTracking()
        .Where(p => p.RentalMode == RentalMode.Short && p.IsActive && p.PublishedOnSite);
    private IQueryable<OctorateListingMapping> ConfirmedMappings =>
        from mapping in db.OctorateListingMappings.IgnoreQueryFilters([Tenant])
        join connection in db.OctorateConnections.IgnoreQueryFilters([Tenant]) on mapping.ConnectionId equals connection.Id
        where connection.Environment == _environment && mapping.Status == OctorateMappingStatus.Confirmed
        select mapping;

    public async Task<PreparationCandidates> CandidatesAsync(string? search, CancellationToken ct)
    {
        const int limit = 300;
        var masterId = options.Value.MasterOrganizationId;
        var term = search?.Trim().ToLowerInvariant();
        var candidates = from property in PublishedShortRentProperties
                         join org in db.Orgs.AsNoTracking() on property.OrgId equals org.Id
                         where property.OrgId != masterId
                             && !Rows.Any(row => row.OrgId == property.OrgId && row.PropertyId == property.Id)
                             && !ConfirmedMappings.Any(mapping => mapping.OrgId == property.OrgId && mapping.PropertyId == property.Id)
                         select new { Property = property, OrganizationName = org.Name, org.ContactEmail, org.ContactPhone };
        if (!string.IsNullOrEmpty(term))
            candidates = candidates.Where(row => row.Property.Name.ToLower().Contains(term) || row.OrganizationName.ToLower().Contains(term));

        var rows = await candidates.OrderBy(row => row.OrganizationName).ThenBy(row => row.Property.Name).ThenBy(row => row.Property.Id)
            .Take(limit + 1)
            .Select(row => new
            {
                Property = new Property
                {
                    Id = row.Property.Id,
                    OrgId = row.Property.OrgId,
                    Name = row.Property.Name,
                    Address = row.Property.Address,
                    City = row.Property.City,
                    PostalCode = row.Property.PostalCode,
                    Country = row.Property.Country,
                    Latitude = row.Property.Latitude,
                    Longitude = row.Property.Longitude,
                },
                row.OrganizationName,
                row.ContactEmail,
                row.ContactPhone,
            }).ToListAsync(ct);
        return new PreparationCandidates(rows.Take(limit).Select(row => new PreparationCandidate(row.Property.OrgId, row.Property.Id,
            row.OrganizationName, row.Property.Name, OctoratePropertyCreator.PropertyErrors(row.Property, row.ContactEmail, row.ContactPhone)
                .Select(error => error.Field).ToArray())).ToArray(), rows.Count > limit);
    }

    public async Task<PreparationBatch> StartAsync(IReadOnlyList<PreparationSelection> selection, CancellationToken ct)
    {
        var (masterId, _) = Settings();
        if (selection.Count is < 1 or > 300 || selection.Any(s => s is null || s.OrgId == masterId)
            || selection.Select(s => s.PropertyId).Distinct().Count() != selection.Count)
            throw Invalid("octorate_preparation_selection");

        if (!await db.OctorateConnections.IgnoreQueryFilters([Tenant]).AnyAsync(c => c.OrgId == masterId && c.Environment == _environment
            && c.Status == OctorateConnectionStatus.Connected && !c.UsesMasterAccount, ct))
            throw Invalid("octorate_master_not_connected");

        var propertyIds = selection.Select(s => s.PropertyId).ToArray();
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.PropertyPreparation, $"batch:{_environment}"));
        var properties = await PublishedShortRentProperties.Where(p => propertyIds.Contains(p.Id)).ToListAsync(ct);
        if (selection.Any(s => !properties.Any(p => p.Id == s.PropertyId && p.OrgId == s.OrgId)))
            throw Invalid("octorate_preparation_selection");

        var existing = await Rows.Where(r => propertyIds.Contains(r.PropertyId)).ToListAsync(ct);
        if (existing.Count > 0)
        {
            if (existing.Count == selection.Count && existing.Select(r => r.BatchId).Distinct().Count() == 1)
                return await ViewAsync(existing[0].BatchId, ct);
            throw Invalid("octorate_preparation_already_exists");
        }

        var batchId = Guid.NewGuid();
        foreach (var item in selection)
            db.OctoratePreparations.Add(new OctoratePreparation
            {
                BatchId = batchId,
                Environment = _environment,
                OrgId = item.OrgId,
                PropertyId = item.PropertyId,
                UpdatedAt = Now,
            });
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return await ViewAsync(batchId, ct);
    }

    public async Task<IReadOnlyList<PreparationRow>> RecentAsync(CancellationToken ct)
    {
        var rows = await Rows.AsNoTracking().OrderByDescending(r => r.UpdatedAt).Take(300).ToListAsync(ct);
        return await DisplayAsync(rows, ct);
    }

    public async Task<PreparationBatch> ViewAsync(Guid batchId, CancellationToken ct)
    {
        var rows = await Rows.AsNoTracking().Where(r => r.BatchId == batchId).OrderBy(r => r.PropertyId).ToListAsync(ct);
        if (rows.Count == 0)
            throw new NotFoundException("Preparation batch not found.") { Code = "octorate_preparation_not_found" };
        return new PreparationBatch(batchId, await DisplayAsync(rows, ct));
    }

    private async Task<IReadOnlyList<PreparationRow>> DisplayAsync(List<OctoratePreparation> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.PropertyId).ToArray();
        var names = await db.Properties.IgnoreQueryFilters([Tenant]).AsNoTracking()
            .Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(r => new PreparationRow(r.Id, r.BatchId, r.OrgId, r.PropertyId,
            names.GetValueOrDefault(r.PropertyId, string.Empty), r.Status.ToString(), r.AccommodationId,
            r.SupplierReference, r.ErrorCode, r.UpdatedAt)).ToArray();
    }

    public async Task RecordSupplierReferenceAsync(Guid batchId, string reference, CancellationToken ct)
    {
        reference = reference.Trim();
        if (reference.Length is < 1 or > 120)
            throw Invalid("octorate_preparation_reference");
        await using var operation = await LockAsync(batchId, ct);
        var rows = await Rows.Where(r => r.BatchId == batchId).ToListAsync(ct);
        if (rows.Count == 0 || rows.Any(r => r.AccommodationId is null))
            throw Invalid("octorate_preparation_reference");
        foreach (var row in rows)
            row.SupplierReference = reference;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReconcileAsync(Guid batchId, Guid rowId, string accommodationId, CancellationToken ct)
    {
        await using var operation = await LockAsync(batchId, ct);
        var (masterId, billingNetwork) = Settings();
        var row = await Rows.SingleOrDefaultAsync(r => r.BatchId == batchId && r.Id == rowId, ct);
        if (row is not { Status: OctoratePreparationStatus.ReconcileCreation or OctoratePreparationStatus.NeedsReview, AccommodationId: null })
            throw Invalid("octorate_preparation_reconcile");
        var property = await ReadPropertyAsync(row, ct);
        using (scope.Use(masterId))
        {
            OctorateEnvironments.EnsureMatches(ExpectedApiEnvironment, await client.GetConfigurationAsync(ct));
            var external = await client.GetPropertyAsync(accommodationId, ct);
            if (!Matches(property, external) || external.NetworkAccessEnabled == false
                || (external.BillingNetworkName is { } network && network != billingNetwork))
                throw Invalid("octorate_preparation_reconcile");
        }
        if (await IsAssignedAsync(accommodationId, ct)
            || await Rows.AnyAsync(r => r.Id != row.Id && r.AccommodationId == accommodationId, ct))
            throw Invalid("octorate_preparation_reconcile");
        row.AccommodationId = accommodationId;
        await UpdateAsync(row, OctoratePreparationStatus.WaitingForLicense, null, ct);
    }

    public async Task RunAsync(Guid batchId, string staffUserId, CancellationToken ct)
    {
        await using var operation = await LockAsync(batchId, ct);
        var rows = await Rows.Where(r => r.BatchId == batchId && r.Status != OctoratePreparationStatus.Ready)
            .OrderBy(r => r.PropertyId).ToListAsync(ct);
        if (rows.Count == 0)
            return;

        var (masterId, billingNetwork) = await VerifyMasterAsync(rows, ct);
        OctorateProperty[]? masterInventory = null;
        foreach (var row in rows)
        {
            try
            {
                if (row.Status == OctoratePreparationStatus.NeedsReview && row.AccommodationId is null
                    && row.ErrorCode is EnvironmentCheckFailed or MasterCheckFailed)
                    await UpdateAsync(row, OctoratePreparationStatus.Queued, null, ct);

                var property = await ReadPropertyAsync(row, ct);
                if (row.Status == OctoratePreparationStatus.Queued ||
                    (row.Status == OctoratePreparationStatus.NeedsReview && row.AccommodationId is null && row.ErrorCode == "octorate_preparation_invalid_data"))
                {
                    if (await IsLinkedAsync(row, ct))
                    {
                        await UpdateAsync(row, OctoratePreparationStatus.NeedsReview, "octorate_preparation_already_linked", ct);
                        continue;
                    }
                    var request = await creator.BuildPreparationRequestAsync(property, staffUserId, billingNetwork, ct);
                    using (scope.Use(masterId))
                    {
                        masterInventory ??= await client.GetPropertiesAsync(ct);
                        var candidate = masterInventory.FirstOrDefault(p => Matches(property, p));
                        if (candidate is not null)
                        {
                            await UpdateAsync(row, OctoratePreparationStatus.ReconcileCreation, "octorate_preparation_candidate_found", ct);
                            continue;
                        }
                        await UpdateAsync(row, OctoratePreparationStatus.ReconcileCreation, "octorate_preparation_creation_uncertain", ct);
                        var created = await client.CreatePropertyAsync(request, ct);
                        row.AccommodationId = created.AccommodationId;
                        row.EncryptedRefreshToken = created.RefreshToken;
                        await UpdateAsync(row, OctoratePreparationStatus.WaitingForLicense, null, CancellationToken.None);
                    }
                }

                if (row.AccommodationId is not null && row.SupplierReference is not null)
                    await VerifyAsync(row, property, masterId, billingNetwork, ct);
                else if (row.ErrorCode is EnvironmentCheckFailed or MasterCheckFailed)
                    await UpdateAsync(row, row.Status, null, ct);
            }
            catch (DomainRuleException)
            {
                await UpdateAsync(row, OctoratePreparationStatus.NeedsReview, "octorate_preparation_invalid_data", ct);
            }
            catch (OctorateRateLimitException)
            {
                throw;
            }
            catch (OctorateException ex)
            {
                var status = ex is OctoratePropertyRejectedException
                    ? OctoratePreparationStatus.NeedsReview : row.Status;
                var code = ex is OctoratePropertyRejectedException ? "octorate_preparation_invalid_data" : ex.Code;
                await UpdateAsync(row, status, code, CancellationToken.None);
            }
        }
    }

    private async Task<(Guid MasterId, string BillingNetwork)> VerifyMasterAsync(
        IReadOnlyList<OctoratePreparation> rows, CancellationToken ct)
    {
        try
        {
            var settings = Settings();
            using (scope.Use(settings.MasterId))
                OctorateEnvironments.EnsureMatches(ExpectedApiEnvironment, await client.GetConfigurationAsync(ct));
            return settings;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception is OctorateWrongEnvironmentException ? EnvironmentCheckFailed : MasterCheckFailed;
            var now = Now;
            foreach (var row in rows)
            {
                if (row.Status == OctoratePreparationStatus.Queued)
                    row.Status = OctoratePreparationStatus.NeedsReview;
                else if (row.ErrorCode is not (null or EnvironmentCheckFailed or MasterCheckFailed))
                    continue;

                row.ErrorCode = code;
                row.UpdatedAt = now;
            }

            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task VerifyAsync(OctoratePreparation row, Property property, Guid masterId, string billingNetwork, CancellationToken ct)
    {
        var acc = row.AccommodationId!;
        OctorateRoom[] rooms;
        using (scope.Use(masterId))
        {
            var external = await client.GetPropertyAsync(acc, ct);
            var license = await client.GetAccommodationLicenseAsync(acc, ct);
            if (external.NetworkAccessEnabled == false || (external.BillingNetworkName is { } network && network != billingNetwork)
                || string.IsNullOrWhiteSpace(license.Tier) || license.Tier.Equals("FREE", StringComparison.OrdinalIgnoreCase)
                || license.RoomsLimit is <= 0 || license.ExpireDay < DateOnly.FromDateTime(Now))
            {
                await UpdateAsync(row, OctoratePreparationStatus.WaitingForLicense, "octorate_preparation_license_pending", ct);
                return;
            }
            rooms = await client.GetRoomsAsync(acc, ct);
            if (rooms.Length == 0 && row.ProductCreationRequestedAt is null)
            {
                row.ProductCreationRequestedAt = Now;
                await db.SaveChangesAsync(ct);
                await client.CreateRoomAsync(acc, new OctorateRoomCreate
                {
                    Name = property.Name,
                    RateNames = new Dictionary<string, string> { ["IT"] = property.Name },
                    Adults = property.MaxGuests,
                    Bedrooms = property.Bedrooms,
                    Beds = property.Beds ?? property.MaxGuests,
                    Bathrooms = property.Bathrooms,
                }, ct);
                rooms = await client.GetRoomsAsync(acc, ct);
            }
        }
        var matchingRooms = rooms.Where(r => string.Equals(r.Name, property.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        var product = rooms.Length == 1 ? rooms[0].Id : matchingRooms.Length == 1 ? matchingRooms[0].Id : null;
        if (product is null)
        {
            await UpdateAsync(row, OctoratePreparationStatus.NeedsReview, "octorate_preparation_product_required", ct);
            return;
        }

        await using var assignment = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.PropertyPreparation, $"assignment-org:{_environment}:{row.OrgId:N}"),
            (PostgresAdvisoryLocks.Scope.PropertyPreparation, $"assignment-acc:{_environment}:{acc}"));
        if (await IsAssignedAsync(acc, ct) || await IsLinkedAsync(row, ct))
        {
            await UpdateAsync(row, OctoratePreparationStatus.NeedsReview, "octorate_preparation_already_linked", ct);
            return;
        }
        var connection = await db.OctorateConnections.IgnoreQueryFilters([Tenant])
            .SingleOrDefaultAsync(c => c.OrgId == row.OrgId && c.Environment == _environment, ct);
        if (connection is { UsesMasterAccount: false })
        {
            await UpdateAsync(row, OctoratePreparationStatus.NeedsReview, "octorate_preparation_existing_account", ct);
            return;
        }
        if (connection is null)
        {
            connection = new OctorateConnection
            {
                OrgId = row.OrgId,
                Environment = _environment,
                UsesMasterAccount = true,
                Status = OctorateConnectionStatus.Connected,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            db.OctorateConnections.Add(connection);
        }
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = row.OrgId,
            ConnectionId = connection.Id,
            PropertyId = property.Id,
            OctorateAccommodationId = acc,
            OctorateProductId = product,
            Status = OctorateMappingStatus.Confirmed,
            ConfirmedAt = Now,
            SnapshotName = property.Name[..Math.Min(property.Name.Length, OctorateListingMapping.SnapshotNameMaxLength)],
        });
        await UpdateAsync(row, OctoratePreparationStatus.Ready, null, ct);
        if (assignment is not null)
            await assignment.CommitAsync(ct);
    }

    private Task<bool> IsAssignedAsync(string acc, CancellationToken ct) =>
        ConfirmedMappings.AnyAsync(mapping => mapping.OctorateAccommodationId == acc, ct);

    private Task<bool> IsLinkedAsync(OctoratePreparation row, CancellationToken ct) =>
        ConfirmedMappings.AnyAsync(mapping => mapping.OrgId == row.OrgId && mapping.PropertyId == row.PropertyId, ct);

    private async Task<Property> ReadPropertyAsync(OctoratePreparation row, CancellationToken ct) =>
        await PublishedShortRentProperties.SingleOrDefaultAsync(p => p.Id == row.PropertyId && p.OrgId == row.OrgId, ct)
        ?? throw new DomainRuleException("octorate_preparation_invalid_data", "PropertyImportInvalid");

    private async Task UpdateAsync(OctoratePreparation row, OctoratePreparationStatus status, string? code, CancellationToken ct)
    {
        row.Status = status;
        row.ErrorCode = code;
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    private (Guid MasterId, string BillingNetwork) Settings() =>
        options.Value.MasterOrganizationId is { } id && id != Guid.Empty && options.Value.BillingNetworkName?.Trim() is { Length: > 0 and <= 64 } network
            ? (id, network) : throw Invalid("octorate_preparation_not_configured");

    private async Task<IAsyncDisposable> LockAsync(Guid batchId, CancellationToken ct) =>
        await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(db, PostgresAdvisoryLocks.Scope.PropertyPreparation, batchId.ToString("N"), ct)
        ?? throw Invalid("octorate_preparation_busy");

    private static bool Matches(Property p, OctorateProperty external) =>
        string.Equals(p.Name.Trim(), external.Name?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.Address.Trim(), external.Address?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.City.Trim(), external.City?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DomainConflictException Invalid(string code) => new(code, "OctoratePreparationUnavailable");
}
