using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The content of the supplier's showcase (SP-13a, <c>api/supplier/showcase/*</c>) on <see cref="SupplierShowcaseContent"/>: the draft the
/// editor autosaves, the publication with its undo, the pause, and the switch of the bookings that follows them.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> The table is keyed by the supplier org and is <b>not</b> tenant-filtered (the TN-2 allow-list says why: a
/// supplier-only account has no <c>User.OrgId</c>). Every read and write here goes through <see cref="Contents"/>, which carries the
/// explicit <c>OrgId</c> predicate; the only other statement is the insert of a row that has its <c>OrgId</c> set. An architecture
/// test (<c>SupplierShowcaseContentTenancyTests</c>) forbids any other code from using the table.</para>
/// <para><b>Serialization.</b> Every change takes the supplier's content lock
/// (<see cref="PostgresAdvisoryLocks.Scope.SupplierShowcaseContent"/>) and reads the row after taking it, so the version check is decided on
/// the row as it is. On top of that the row carries <c>xmin</c>: a lost race is a 409, never a lost update and never a 500. A change that
/// also writes <c>OnlineBookingEnabled</c> takes the agenda lock <b>after</b> the content lock, always in this order (the supplier repair
/// follows it too), so two requests never wait for each other. Outside PostgreSQL (in-memory tests) nothing is locked.</para>
/// <para><b>The bookings.</b> <c>OnlineBookingEnabled</c> is written only through
/// <see cref="ISupplierAgendaService.SetOnlineBookingEnabledAsync"/>, and only from here, with the value of
/// <see cref="ShowcaseBookings.SwitchValue"/>: published, not paused, the payments on (<c>SupplierOnlinePayments</c>) and the supplier's
/// Stripe account able to take charges and payouts (cached, read from the org: never a call to Stripe). The switch is written
/// <b>before</b> the row is changed, in the same transaction, so a lost race rolls both back.</para>
/// <para><b>What it keeps out of the logs.</b> Ids and counts, never a text of the content.</para>
/// </remarks>
public class SupplierShowcaseContentService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    IFileStorage storage,
    IFeatureFlags features,
    ILogger<SupplierShowcaseContentService> logger,
    TimeProvider? timeProvider = null) : ISupplierShowcaseContentService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseContentView> GetAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var row = await Contents(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task<ShowcaseContentView> SaveDraftAsync(
        Guid supplierOrgId,
        uint version,
        JsonObject patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);

        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        EnsureVersion(row, version, supplierOrgId);

        var document = ShowcaseContentRules.ApplyPatch(row?.DraftJson, patch, ContextOf(supplierOrgId, facts));

        var now = Now();
        row ??= Add(supplierOrgId, now);
        row.DraftJson = ShowcaseContentRules.Write(document);
        row.SavedAt = now;
        row.UpdatedAt = now;
        ClearUndo(row);

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase draft saved", supplierOrgId);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task<ShowcaseContentView> DiscardAsync(
        Guid supplierOrgId,
        uint? version,
        CancellationToken cancellationToken = default)
    {
        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);

        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        if (version is { } expected)
            EnsureVersion(row, expected, supplierOrgId);

        // Nothing saved yet: nothing to throw away.
        if (row is null)
            return await ToViewAsync(supplierOrgId, null, facts, cancellationToken);

        var now = Now();
        row.DraftJson = row.PublishedJson ?? "{}";
        row.SavedAt = now;
        row.UpdatedAt = now;
        ClearUndo(row);

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase draft discarded", supplierOrgId);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task<ShowcaseContentView> PublishAsync(
        Guid supplierOrgId,
        uint? version,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        // Judged after the lock, on the facts as they are now.
        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        if (version is { } expected)
            EnsureVersion(row, expected, supplierOrgId);

        if (facts.Status != SupplierStatus.Active)
            throw ShowcaseContentErrors.NotActive();

        // What the customers will read: the rows left empty dropped, the half-filled ones refused, only the services that are active now.
        var document = ShowcaseContentRules.ForPublication(ShowcaseContentRules.Read(row?.DraftJson), facts.ActiveSlugs);
        var readiness = ShowcaseReadiness.Evaluate(document, ReadinessFactsOf(facts, document));
        if (!readiness.CanPublish)
            throw ShowcaseContentErrors.NotPublishableItems(readiness.Missing);

        // The bookings first, in this transaction: they open only if everything they need is ready, and a lost race undoes both.
        var paused = row?.PausedAt is not null;
        await agenda.SetOnlineBookingEnabledAsync(
            supplierOrgId,
            ShowcaseBookings.SwitchValue(published: true, paused, facts.PaymentsFlagOn, facts.AccountReady),
            cancellationToken);

        var now = Now();
        var json = ShowcaseContentRules.Write(document);
        row ??= Add(supplierOrgId, now);

        // The version published before is kept for "Annulla"; the draft becomes what is published (it was cleaned of its empty rows).
        row.PreviousPublishedJson = row.PublishedJson;
        row.PreviousPublishedAt = row.PublishedAt;
        row.PublishedJson = json;
        row.PublishedAt = now;
        row.DraftJson = json;
        row.SavedAt = now;
        row.UpdatedAt = now;
        row.UndoUntil = now + ShowcaseContentLimits.UndoWindow;

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: showcase published", supplierOrgId);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task<ShowcaseContentView> UndoPublishAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        var now = Now();
        if (row is null || row.PublishedJson is null || row.UndoUntil is not { } until || until <= now)
        {
            logger.LogInformation("Supplier {OrgId}: showcase undo refused, nothing to undo", supplierOrgId);
            throw ShowcaseContentErrors.NothingToUndo();
        }

        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);

        // Back to the version before (none, for the first publication: the showcase is a draft again); the draft stays as it is.
        await agenda.SetOnlineBookingEnabledAsync(
            supplierOrgId,
            ShowcaseBookings.SwitchValue(
                published: row.PreviousPublishedJson is not null,
                paused: row.PausedAt is not null,
                facts.PaymentsFlagOn,
                facts.AccountReady),
            cancellationToken);

        row.PublishedJson = row.PreviousPublishedJson;
        row.PublishedAt = row.PreviousPublishedAt;
        row.UpdatedAt = now;
        ClearUndo(row);

        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: last showcase publication undone", supplierOrgId);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task<ShowcaseContentView> PauseAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        if (row?.PublishedJson is null)
            throw ShowcaseContentErrors.NothingPublished();

        if (row.PausedAt is null)
        {
            var now = Now();
            await agenda.SetOnlineBookingEnabledAsync(supplierOrgId, false, cancellationToken);

            row.PausedAt = now;
            row.UpdatedAt = now;
            ClearUndo(row);

            await SaveAsync(cancellationToken);
            logger.LogInformation("Supplier {OrgId}: showcase paused", supplierOrgId);
        }

        await CommitAsync(transaction, cancellationToken);
        return await ToViewAsync(supplierOrgId, row, await LoadFactsAsync(supplierOrgId, cancellationToken), cancellationToken);
    }

    public async Task<ShowcaseContentView> ResumeAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        var facts = await LoadFactsAsync(supplierOrgId, cancellationToken);
        var row = await Contents(supplierOrgId).FirstOrDefaultAsync(cancellationToken);
        if (row?.PublishedJson is null)
            throw ShowcaseContentErrors.NothingPublished();

        if (facts.Status != SupplierStatus.Active)
            throw ShowcaseContentErrors.NotActive();

        // Reopens a paused showcase and, on one that is not paused, judges the bookings again (the account became ready since).
        await agenda.SetOnlineBookingEnabledAsync(
            supplierOrgId,
            ShowcaseBookings.SwitchValue(published: true, paused: false, facts.PaymentsFlagOn, facts.AccountReady),
            cancellationToken);

        if (row.PausedAt is not null)
        {
            var now = Now();
            row.PausedAt = null;
            row.UpdatedAt = now;
            ClearUndo(row);

            await SaveAsync(cancellationToken);
            logger.LogInformation("Supplier {OrgId}: showcase reopened", supplierOrgId);
        }

        await CommitAsync(transaction, cancellationToken);
        return await ToViewAsync(supplierOrgId, row, facts, cancellationToken);
    }

    public async Task ReconcileBookingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAsync(supplierOrgId, cancellationToken);

        // A supplier that never used the editor has never had its bookings opened by it: nothing to bring in line.
        var row = await Contents(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return;

        var (paymentsFlagOn, accountReady) = await PaymentsAsync(supplierOrgId, cancellationToken);
        await agenda.SetOnlineBookingEnabledAsync(
            supplierOrgId,
            ShowcaseBookings.SwitchValue(row.PublishedJson is not null, row.PausedAt is not null, paymentsFlagOn, accountReady),
            cancellationToken);

        await CommitAsync(transaction, cancellationToken);
    }

    public async Task<PublishedShowcase?> GetPublishedAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var row = await Contents(supplierOrgId)
            .AsNoTracking()
            .Select(c => new { c.PublishedJson, c.PausedAt, c.PublishedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.PublishedJson is null
            ? null
            : new PublishedShowcase(ShowcaseContentRules.Read(row.PublishedJson), row.PausedAt is not null, row.PublishedAt);
    }

    public async Task<ShowcasePublication> GetPublicationAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var row = await Contents(supplierOrgId)
            .AsNoTracking()
            .Select(c => new { Published = c.PublishedJson != null, c.PausedAt, c.PublishedAt, c.SavedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? new ShowcasePublication(false, false, false, null, null)
            : new ShowcasePublication(true, row.Published, row.PausedAt is not null, row.PublishedAt, row.SavedAt);
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

    /// <summary>A new row of <paramref name="orgId"/>, added to the context.</summary>
    private SupplierShowcaseContent Add(Guid orgId, DateTime now)
    {
        var row = new SupplierShowcaseContent { OrgId = orgId, CreatedAt = now };
        db.SupplierShowcaseContents.Add(row);
        return row;
    }

    /// <summary>The view of a row; the empty one (version 0, nothing saved) for a supplier that has none.</summary>
    private async Task<ShowcaseContentView> ToViewAsync(
        Guid orgId,
        SupplierShowcaseContent? row,
        Facts facts,
        CancellationToken cancellationToken)
    {
        var draft = ShowcaseContentRules.Read(row?.DraftJson);
        var published = row?.PublishedJson is null ? null : ShowcaseContentRules.Read(row.PublishedJson);
        var changes = ShowcaseContentRules.Changes(published, draft);
        var paused = row?.PausedAt is not null;
        var state = ShowcaseContentRules.StateOf(published is not null, paused, changes.Count > 0);
        DateTime? undoUntil = row?.UndoUntil is { } until && until > Now() ? until : null;

        var cleaned = ShowcaseContentRules.Cleaned(draft);
        var readiness = ShowcaseReadiness.Evaluate(cleaned, ReadinessFactsOf(facts, cleaned));
        var switchOn = (await agenda.GetBookingSettingsAsync(orgId, cancellationToken)).OnlineBookingEnabled;
        var bookings = ShowcaseBookings.Evaluate(published is not null, paused, switchOn, facts.PaymentsFlagOn, facts.AccountReady);

        return new ShowcaseContentView(
            row?.Version ?? 0,
            state,
            draft,
            published,
            changes,
            row?.SavedAt,
            row?.PublishedAt,
            row?.PausedAt,
            undoUntil,
            readiness,
            bookings);
    }

    // ─── What the readiness and the bookings are judged on ───────────────────────

    /// <summary>The facts about the supplier the rules need, read once for a request.</summary>
    private sealed class Facts
    {
        public SupplierStatus? Status { get; init; }

        public bool HasZones { get; init; }

        public bool HoursConfigured { get; init; }

        public string? VatNumber { get; init; }

        public HashSet<string> ActiveSlugs { get; init; } = [];

        public bool PaymentsFlagOn { get; init; }

        public bool AccountReady { get; init; }
    }

    private async Task<Facts> LoadFactsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var profile = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == orgId)
            .Select(sp => new { sp.Status, sp.VatNumber, sp.ComuniJson, sp.ComuneIstatCodesJson })
            .FirstOrDefaultAsync(cancellationToken);

        var services = await catalog.ListAsync(orgId, cancellationToken);
        var hours = await agenda.GetHoursAsync(orgId, cancellationToken);
        var (paymentsFlagOn, accountReady) = await PaymentsAsync(orgId, cancellationToken);

        return new Facts
        {
            Status = profile?.Status,
            VatNumber = profile?.VatNumber,
            HasZones = profile is not null
                       && (SupplierComuneMatcher.ReadStrings(profile.ComuniJson).Count > 0
                           || SupplierComuneMatcher.ReadStrings(profile.ComuneIstatCodesJson).Count > 0),
            HoursConfigured = hours.ConfiguredAt is not null,
            ActiveSlugs = services
                .Where(service => service.Status == SupplierServiceListingStatus.Active)
                .Select(service => service.Slug)
                .ToHashSet(StringComparer.Ordinal),
            PaymentsFlagOn = paymentsFlagOn,
            AccountReady = accountReady,
        };
    }

    /// <summary>
    /// Whether the payments inside CasaZen are on (<c>SupplierOnlinePayments</c>) and whether the supplier's Stripe account can take charges
    /// and payouts, as the database caches it from <c>account.updated</c> (never a call to Stripe).
    /// </summary>
    private async Task<(bool PaymentsFlagOn, bool AccountReady)> PaymentsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var account = await db.Orgs
            .AsNoTracking()
            .Where(org => org.Id == orgId)
            .Select(org => new { org.StripeConnectedAccountId, org.ConnectChargesEnabled, org.ConnectPayoutsEnabled })
            .FirstOrDefaultAsync(cancellationToken);

        var ready = account is not null
                    && SupplierVerification.CanReceivePayments(
                        account.StripeConnectedAccountId, account.ConnectChargesEnabled, account.ConnectPayoutsEnabled);
        return (features.IsEnabled(FeatureFlags.SupplierOnlinePayments), ready);
    }

    private static ShowcaseReadinessFacts ReadinessFactsOf(Facts facts, ShowcaseContentDocument document)
    {
        // The services shown: the ones of the order that are active, or every active one when the order is empty.
        var shown = document.ServiceOrder.Count > 0
            ? document.ServiceOrder.Count(facts.ActiveSlugs.Contains)
            : facts.ActiveSlugs.Count;
        var verified = facts.Status is { } status && SupplierVerification.IsVerified(status, facts.AccountReady, facts.VatNumber);

        return new ShowcaseReadinessFacts(shown, facts.HasZones, facts.HoursConfigured, verified);
    }

    // ─── Rules, locks, saving ────────────────────────────────────────────────────

    /// <summary>
    /// What the rules need that is not in the content: the supplier's active services (a service paused or deleted is not shown) and
    /// whether an image is an object of the supplier's own folder of the public storage.
    /// </summary>
    private ShowcaseContentContext ContextOf(Guid orgId, Facts facts) =>
        new(url => IsOwnImage(orgId, url), facts.ActiveSlugs);

    /// <summary>
    /// True for the URL of an object in the supplier's own folder of the public bucket (<c>suppliers/{orgId}/</c>): the photos of its
    /// profile and services and, later, the media of its showcase. A content can never point to somebody else's file or to a site.
    /// </summary>
    private bool IsOwnImage(Guid orgId, string url)
    {
        var key = storage.TryGetPublicKey(url);
        return key is not null
               && StorageKeys.IsValid(key)
               && key.StartsWith(StorageKeys.SupplierFolder(orgId), StringComparison.Ordinal);
    }

    /// <summary>The version the client read must be the current one: another tab, the wizard or another member changed it since.</summary>
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
