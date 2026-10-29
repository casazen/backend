using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The content of the supplier's showcase (SP-13a, <c>api/supplier/showcase/content</c>) on <see cref="SupplierShowcaseContent"/>.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> The table is keyed by the supplier org and is <b>not</b> tenant-filtered (the TN-2 allow-list says why: a
/// supplier-only account has no <c>User.OrgId</c>). Every read and write here goes through <see cref="Contents"/>, which carries the
/// explicit <c>OrgId</c> predicate; the only other statement is the insert of a row that has its <c>OrgId</c> set. An architecture
/// test (<c>SupplierShowcaseContentTenancyTests</c>) forbids any other code from using the table.</para>
/// <para><b>Serialization.</b> Every change takes the supplier's content lock
/// (<see cref="PostgresAdvisoryLocks.Scope.SupplierShowcaseContent"/>) and reads the row after taking it, so the version check is decided on
/// the row as it is. On top of that the row carries <c>xmin</c>: a lost race is a 409, never a lost update and never a 500. Outside
/// PostgreSQL (in-memory tests) nothing is locked.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never a text of the content.</para>
/// </remarks>
public class SupplierShowcaseContentService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    IFileStorage storage,
    ILogger<SupplierShowcaseContentService> logger,
    TimeProvider? timeProvider = null) : ISupplierShowcaseContentService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseContentView> GetAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var row = await Contents(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return ToView(row);
    }

    public async Task<ShowcaseContentView> SaveDraftAsync(
        Guid supplierOrgId,
        uint version,
        JsonObject patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var context = await ContextAsync(supplierOrgId, cancellationToken);

        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        EnsureVersion(row, version, supplierOrgId);

        var document = ShowcaseContentRules.ApplyPatch(row?.DraftJson, patch, context);

        var now = Now();
        if (row is null)
        {
            row = new SupplierShowcaseContent { OrgId = supplierOrgId, CreatedAt = now };
            db.SupplierShowcaseContents.Add(row);
        }

        row.DraftJson = ShowcaseContentRules.Write(document);
        row.SavedAt = now;
        row.UpdatedAt = now;
        ClearUndo(row);

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase draft saved", supplierOrgId);
        return ToView(row);
    }

    public async Task<ShowcaseContentView> DiscardAsync(
        Guid supplierOrgId,
        uint? version,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        if (version is { } expected)
            EnsureVersion(row, expected, supplierOrgId);

        // Nothing saved yet: nothing to throw away.
        if (row is null)
            return ToView(null);

        var now = Now();
        row.DraftJson = row.PublishedJson ?? "{}";
        row.SavedAt = now;
        row.UpdatedAt = now;
        ClearUndo(row);

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase draft discarded", supplierOrgId);
        return ToView(row);
    }

    /// <summary>
    /// The content of <paramref name="orgId"/>. <b>The only way</b> this service reads or changes a row: the table is not
    /// tenant-filtered, so the supplier org is always an explicit predicate.
    /// </summary>
    private IQueryable<SupplierShowcaseContent> Contents(Guid orgId) => ContentsOf(db, orgId);

    /// <inheritdoc cref="Contents"/>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<SupplierShowcaseContent> ContentsOf(AppDbContext db, Guid orgId) =>
        db.SupplierShowcaseContents.Where(c => c.OrgId == orgId);

    /// <summary>The view of a row; the empty one (version 0, nothing saved) for a supplier that has none.</summary>
    private ShowcaseContentView ToView(SupplierShowcaseContent? row)
    {
        if (row is null)
            return new ShowcaseContentView(0, ShowcaseContentState.Draft, new ShowcaseContentDocument(), null, [], null, null, null, null);

        var draft = ShowcaseContentRules.Read(row.DraftJson);
        var published = row.PublishedJson is null ? null : ShowcaseContentRules.Read(row.PublishedJson);
        var changes = ShowcaseContentRules.Changes(published, draft);
        var state = ShowcaseContentRules.StateOf(published is not null, row.PausedAt is not null, changes.Count > 0);
        DateTime? undoUntil = row.UndoUntil is { } until && until > Now() ? until : null;

        return new ShowcaseContentView(
            row.Version, state, draft, published, changes, row.SavedAt, row.PublishedAt, row.PausedAt, undoUntil);
    }

    /// <summary>
    /// What the rules need that is not in the content: the supplier's active services (a service paused or deleted is not shown) and
    /// whether an image is an object of the supplier's own folder of the public storage.
    /// </summary>
    private async Task<ShowcaseContentContext> ContextAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var services = await catalog.ListAsync(orgId, cancellationToken);
        var active = services
            .Where(service => service.Status == SupplierServiceListingStatus.Active)
            .Select(service => service.Slug)
            .ToHashSet(StringComparer.Ordinal);

        return new ShowcaseContentContext(url => IsOwnImage(orgId, url), active);
    }

    /// <summary>
    /// True for the URL of an object in the supplier's own folder of the public bucket (<c>suppliers/{orgId}/</c>): the photos of its
    /// profile and services and, later, the media of the showcase. A content can never point to somebody else's file or to a site.
    /// </summary>
    private bool IsOwnImage(Guid orgId, string url)
    {
        var key = storage.TryGetPublicKey(url);
        return key is not null
               && StorageKeys.IsValid(key)
               && key.StartsWith(StorageKeys.SupplierFolder(orgId), StringComparison.Ordinal);
    }

    /// <summary>The version the client read must be the current one: another tab, the wizard or another member saved since.</summary>
    private void EnsureVersion(SupplierShowcaseContent? row, uint version, Guid orgId)
    {
        if ((row?.Version ?? 0) == version)
            return;

        logger.LogInformation("Supplier {OrgId}: showcase change refused, the version is stale", orgId);
        throw ShowcaseContentErrors.Conflict();
    }

    /// <summary>Anything but a publication ends the possibility of undoing the last one.</summary>
    private static void ClearUndo(SupplierShowcaseContent row)
    {
        row.UndoUntil = null;
        row.PreviousPublishedJson = null;
        row.PreviousPublishedAt = null;
    }

    private Task<IDbContextTransaction?> LockAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierShowcaseContent, orgId.ToString("N")));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Saves, turning a lost race into 409 <c>showcase_version_conflict</c>: a concurrent change of the row (<c>xmin</c>) or the
    /// insert of the first row by two requests at once. Under the content lock neither should happen; this is the guarantee if it does.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Supplier showcase content not saved: the row changed since it was read");
            throw ShowcaseContentErrors.Conflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Supplier showcase content not saved: the row was created meanwhile");
            throw ShowcaseContentErrors.Conflict();
        }
    }
}
