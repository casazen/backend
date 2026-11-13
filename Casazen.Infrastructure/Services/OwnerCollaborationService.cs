using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The collaboration of the owner portal: shared documents, delegated contacts and the inbox (E07/F07/US01/T02, NF-111). See
/// <see cref="IOwnerCollaborationService"/>.
/// </summary>
/// <remarks>
/// <para><b>No tenant of the caller.</b> The person of the owner portal has no org: every read and write ignores the tenant filter of the request and names
/// the org, the owner profile and the property explicitly, after the <see cref="OwnerPortalAccess"/> of the caller (resolved at every request) said it
/// reaches them. The agency methods filter on the org the web layer resolved, the same way.</para>
/// <para><b>Files.</b> A file is scanned (<see cref="IOwnerFileScanner"/>) before it is stored in the private bucket under a key the server makes; a file the
/// scan refuses is never stored. A save that fails after the file was stored removes the file again.</para>
/// </remarks>
public sealed partial class OwnerCollaborationService(
    AppDbContext db,
    IFileStorage storage,
    IOwnerFileScanner scanner,
    IOwnerPortalAccessResolver accessResolver,
    IActivityLog activityLog,
    IInternalActivityService activities,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IAuth0ManagementService auth0Management,
    IOwnerContactIndex contactIndex,
    TimeProvider timeProvider,
    ILogger<OwnerCollaborationService> logger) : IOwnerCollaborationService
{
    /// <summary>The entity type of the «To do» about a document that lapses.</summary>
    public const string DocumentVersionEntityType = "OwnerDocumentVersion";

    /// <summary>The line of a «To do» about a document that lapses: a code, never a title.</summary>
    public const string ExpiringReason = "owner_document_expiring";

    // ---------------------------------------------------------------- documents: owner portal

    public async Task<OwnerPortalDocuments> ListDocumentsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var readsDocuments = access.With(OwnerPortalPermission.DocumentsRead).Count > 0;
        var readsAccounting = access.With(OwnerPortalPermission.AccountingRead).Count > 0;
        if (!readsDocuments && !readsAccounting)
            throw new DomainForbiddenException(OwnerPortalErrors.PermissionMissing, OwnerPortalErrors.PermissionMissingMessageKey);

        var ownerIds = access.Grants.Select(g => g.PropertyOwnerId).Distinct().ToList();
        var documents = readsDocuments
            ? (await Documents().AsNoTracking().Where(d => ownerIds.Contains(d.PropertyOwnerId) && d.RevokedAt == null).ToListAsync(cancellationToken))
                .Where(d => CanRead(access, d))
                .ToList()
            : [];

        var statements = new List<OwnerStatementReferenceView>();
        if (readsAccounting)
        {
            // The statements of the owner accounting (E06), referenced where they are: the published versions and the ones a later version superseded.
            var versions = await db.OwnerStatementVersions
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(v => ownerIds.Contains(v.PropertyOwnerId) && v.PublishedAt != null)
                .OrderByDescending(v => v.PeriodYear)
                .ThenByDescending(v => v.PeriodMonth)
                .ThenByDescending(v => v.VersionNumber)
                .Take(OwnerCollaborationRules.MaxList)
                .ToListAsync(cancellationToken);
            statements.AddRange(versions
                .Where(v => access.Covers(v.OrgId, v.PropertyOwnerId, OwnerPortalPermission.AccountingRead))
                .Select(v => new OwnerStatementReferenceView(
                    v.Id, v.OrgId, v.PropertyOwnerId, v.PeriodYear, v.PeriodMonth, v.VersionNumber, v.Status, v.PublishedAt!.Value, v.PublishedSha256)));
        }

        return new OwnerPortalDocuments(await DocumentViewsAsync(documents, cancellationToken), statements);
    }

    public async Task<OwnerFileContent> OpenDocumentForOwnerAsync(
        OwnerPortalAccess access,
        Guid documentId,
        Guid? versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.Require(OwnerPortalPermission.DocumentsRead);

        // Withdrawn, of another owner or agency, outside the property the caller reaches, a statement without accounting.read, missing: the same 404.
        var document = await Documents().AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.RevokedAt == null, cancellationToken);
        if (document is null || !CanRead(access, document))
            throw DocumentNotFound(documentId);

        var version = await VersionOfAsync(document.Id, versionId, cancellationToken) ?? throw DocumentNotFound(documentId);
        return await HandOutAsync(document, version, access.OwnerUserId, null, cancellationToken);
    }

    // ---------------------------------------------------------------- documents: agency

    public async Task<IReadOnlyList<OwnerDocumentAdminView>> ListDocumentsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default)
    {
        var query = Documents().AsNoTracking().Where(d => d.OrgId == orgId);
        if (propertyOwnerId is { } ownerId)
            query = query.Where(d => d.PropertyOwnerId == ownerId);

        var documents = await query.OrderByDescending(d => d.UpdatedAt).Take(OwnerCollaborationRules.MaxList).ToListAsync(cancellationToken);
        return await AdminViewsAsync(documents, cancellationToken);
    }

    public async Task<OwnerDocumentAdminView> ShareDocumentAsync(
        Guid orgId,
        ShareOwnerDocument command,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var title = (command.Title ?? string.Empty).Trim();
        RequireDocument(title.Length is > 0 and <= OwnerDocument.TitleMaxLength, "title");
        RequireDocument(Enum.IsDefined(command.Type), "type");
        RequireDocument(command.EffectiveFrom is null || command.ExpiresOn is null || command.ExpiresOn >= command.EffectiveFrom, "expiresOn");
        await EnsureOwnerTargetAsync(orgId, command.PropertyOwnerId, command.PropertyId, cancellationToken);

        var verdict = Scan(file);
        var now = Now();
        var document = new OwnerDocument
        {
            OrgId = orgId,
            PropertyOwnerId = command.PropertyOwnerId,
            PropertyId = command.PropertyId,
            Type = command.Type,
            Title = title,
            CreatedAt = now,
            CreatedByUserId = actorUserId,
            UpdatedAt = now,
        };
        var version = NewVersion(document, 1, file, verdict, command.EffectiveFrom, command.ExpiresOn, actorUserId, now);

        await StoreAsync(version.StorageKey, file.Content, version.ContentType, cancellationToken);
        db.OwnerDocuments.Add(document);
        db.OwnerDocumentVersions.Add(version);
        Audit(orgId, document.Id, OwnerPortalParts.Document, OwnerPortalAuditStatuses.Shared, actorUserId);
        await SaveOrForgetFileAsync(version.StorageKey, cancellationToken);

        logger.LogInformation("Owner document {DocumentId} shared by user {UserId}: version 1, {Bytes} bytes", document.Id, actorUserId, version.SizeBytes);
        return (await AdminViewsAsync([document], cancellationToken))[0];
    }

    public async Task<OwnerDocumentAdminView> AddDocumentVersionAsync(
        Guid orgId,
        Guid documentId,
        OwnerDocumentVersionInput input,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        RequireDocument(input.EffectiveFrom is null || input.ExpiresOn is null || input.ExpiresOn >= input.EffectiveFrom, "expiresOn");

        var document = await Documents().FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId, cancellationToken)
            ?? throw DocumentNotFound(documentId);
        var verdict = Scan(file);
        var number = 1 + await Versions().Where(v => v.OwnerDocumentId == document.Id).Select(v => (int?)v.VersionNumber).MaxAsync(cancellationToken) ?? 1;
        var now = Now();
        var version = NewVersion(document, number, file, verdict, input.EffectiveFrom, input.ExpiresOn, actorUserId, now);

        await StoreAsync(version.StorageKey, file.Content, version.ContentType, cancellationToken);
        db.OwnerDocumentVersions.Add(version);
        document.UpdatedAt = now;
        Audit(orgId, document.Id, OwnerPortalParts.Document, OwnerPortalAuditStatuses.Versioned, actorUserId);
        await SaveOrForgetFileAsync(version.StorageKey, cancellationToken);

        logger.LogInformation("Owner document {DocumentId}: version {Version} added by user {UserId}", document.Id, number, actorUserId);
        return (await AdminViewsAsync([document], cancellationToken))[0];
    }

    public async Task<OwnerDocumentAdminView> SetDocumentVisibilityAsync(
        Guid orgId,
        Guid documentId,
        bool visible,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var document = await Documents().FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId, cancellationToken)
            ?? throw DocumentNotFound(documentId);

        // A repeat changes nothing and writes no line.
        if (visible == (document.RevokedAt is null))
            return (await AdminViewsAsync([document], cancellationToken))[0];

        var now = Now();
        document.RevokedAt = visible ? null : now;
        document.RevokedByUserId = visible ? null : actorUserId;
        document.UpdatedAt = now;
        Audit(orgId, document.Id, OwnerPortalParts.Document, visible ? OwnerPortalAuditStatuses.Restored : OwnerPortalAuditStatuses.Revoked, actorUserId);
        await db.SaveChangesAsync(cancellationToken);
        return (await AdminViewsAsync([document], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<OwnerDocumentDownloadView>> ListDocumentDownloadsAsync(Guid orgId, Guid documentId, CancellationToken cancellationToken = default)
    {
        if (!await Documents().AnyAsync(d => d.Id == documentId && d.OrgId == orgId, cancellationToken))
            throw DocumentNotFound(documentId);

        var downloads = await db.OwnerDocumentDownloads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OwnerDocumentId == documentId && d.OrgId == orgId)
            .OrderByDescending(d => d.DownloadedAt)
            .Take(OwnerCollaborationRules.MaxList)
            .ToListAsync(cancellationToken);
        var numbers = await Versions().AsNoTracking()
            .Where(v => v.OwnerDocumentId == documentId)
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber, cancellationToken);
        var people = await OwnerUserNamesAsync(downloads.Where(d => d.OwnerUserId != null).Select(d => d.OwnerUserId!.Value), cancellationToken);

        return downloads
            .Select(d => new OwnerDocumentDownloadView(
                d.OwnerDocumentVersionId,
                numbers.GetValueOrDefault(d.OwnerDocumentVersionId),
                d.DownloadedAt,
                d.OwnerUserId is { } person ? people.GetValueOrDefault(person, string.Empty) : d.UserId ?? string.Empty,
                d.OwnerUserId is null))
            .ToList();
    }

    public async Task<OwnerFileContent> OpenDocumentForAgencyAsync(
        Guid orgId,
        Guid documentId,
        Guid versionId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var document = await Documents().AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId, cancellationToken)
            ?? throw DocumentNotFound(documentId);
        var version = await VersionOfAsync(document.Id, versionId, cancellationToken) ?? throw DocumentNotFound(documentId);
        return await HandOutAsync(document, version, null, actorUserId, cancellationToken);
    }

    /// <summary>The documents one page of the expiry alerts looks at.</summary>
    private const int AlertPageSize = 200;

    /// <summary>The most pages one run reads (200,000 documents): a guard, never reached by a real portfolio.</summary>
    private const int MaxAlertPages = 1_000;

    public async Task<OwnerDocumentAlertResult> RaiseExpiryAlertsAsync(CancellationToken cancellationToken = default)
    {
        var today = Today();
        var horizon = today.AddDays(OwnerCollaborationRules.ExpiryAlertDays);
        var typeName = nameof(ActivityType.OwnerDocumentExpiring);

        // The versions in force that lapse within the window: in force today (no start date, or one that has come) and not replaced by a later version that is in force
        // too. The subqueries are built outside the predicates: an expression tree cannot hold the list of the filters to ignore. The soft delete filter of the
        // properties stays on: a document of a deleted property has no property to raise the item on.
        var inForce = Versions().Where(v => v.EffectiveFrom == null || v.EffectiveFrom <= today);
        var lapsing = inForce.Where(v => v.ExpiresOn != null
                                         && v.ExpiresOn <= horizon
                                         && !inForce.Any(later => later.OwnerDocumentId == v.OwnerDocumentId && later.VersionNumber > v.VersionNumber));
        var properties = db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

        var open = await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(a => a.Action == ActivityAction.RenewOwnerDocument && a.EntityType == DocumentVersionEntityType && a.CompletedAt == null && a.EntityId != null)
            .Select(a => new { a.Id, VersionId = a.EntityId!.Value })
            .ToListAsync(cancellationToken);

        // Every one of them, soonest first, a page at a time: nothing is left unread however many documents lapse (the first 500 of a list without an order used to
        // be the only ones ever looked at). One document that cannot be raised never stops the others: it is logged, the run goes on and ends in error.
        var alerting = new HashSet<Guid>();
        var raised = 0;
        Exception? firstFailure = null;
        for (var page = 0; page < MaxAlertPages; page++)
        {
            var rows = await lapsing
                .AsNoTracking()
                .OrderBy(v => v.ExpiresOn)
                .ThenBy(v => v.Id)
                .Skip(page * AlertPageSize)
                .Take(AlertPageSize)
                .Select(v => new { v.Id, v.OwnerDocumentId, v.ExpiresOn })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;

            var documentIds = rows.Select(r => r.OwnerDocumentId).Distinct().ToList();
            var documents = await Documents().AsNoTracking()
                .Where(d => documentIds.Contains(d.Id) && d.RevokedAt == null)
                .ToDictionaryAsync(d => d.Id, cancellationToken);
            var propertyIds = documents.Values.Where(d => d.PropertyId != null).Select(d => d.PropertyId!.Value).Distinct().ToList();
            var liveProperties = (await properties.Where(p => propertyIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken)).ToHashSet();
            var versionIds = rows.Select(r => r.Id).ToList();
            var alreadyRaised = (await db.InternalActivities
                    .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                    .AsNoTracking()
                    .Where(a => a.Action != null && a.ActivityType == typeName && a.EntityType == DocumentVersionEntityType && a.EntityId != null && versionIds.Contains(a.EntityId.Value))
                    .Select(a => a.EntityId!.Value)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            foreach (var row in rows)
            {
                if (!documents.TryGetValue(row.OwnerDocumentId, out var document) || row.ExpiresOn is not { } expiresOn)
                    continue;
                if (document.PropertyId is { } propertyId && !liveProperties.Contains(propertyId))
                    continue;

                alerting.Add(row.Id);
                if (alreadyRaised.Contains(row.Id))
                    continue;

                try
                {
                    await activities.CreateAsync(
                        new CreateTodoRequest
                        {
                            Type = ActivityType.OwnerDocumentExpiring,
                            Action = ActivityAction.RenewOwnerDocument,
                            EntityType = DocumentVersionEntityType,
                            EntityId = row.Id,
                            Urgency = expiresOn < today ? ActivityUrgency.High : ActivityUrgency.Medium,
                            PropertyId = document.PropertyId,
                            OrgId = document.OrgId,
                            Deadline = expiresOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                            Reason = ExpiringReason,
                        },
                        cancellationToken);
                    raised++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not swallowed: the run ends in error once the rest is done, and the next run tries again.
                    logger.LogError(ex, "Owner document {DocumentId}: the expiry To do of its version {VersionId} could not be raised", document.Id, row.Id);
                    firstFailure ??= ex;
                    db.ChangeTracker.Clear();
                }
            }
        }

        // An alert whose version is no longer in force, is renewed beyond the window, whose document was withdrawn or whose property is gone: closed by the system.
        var closed = 0;
        foreach (var item in open.Where(a => !alerting.Contains(a.VersionId)))
        {
            try
            {
                await activities.CloseAsync(item.Id, InternalActivity.ClosedBySystem, cancellationToken);
                closed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Owner document: the expiry To do {ActivityId} could not be closed", item.Id);
                firstFailure ??= ex;
                db.ChangeTracker.Clear();
            }
        }

        if (firstFailure is not null)
            throw new InvalidOperationException("The expiry alerts of the owner documents failed for at least one document.", firstFailure);

        return new OwnerDocumentAlertResult(raised, closed);
    }

    // ---------------------------------------------------------------- shared

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);

    private DateOnly Today() => timeProvider.TodayInRomeAsDateOnly();

    private IQueryable<OwnerDocument> Documents() => db.OwnerDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private IQueryable<OwnerDocumentVersion> Versions() => db.OwnerDocumentVersions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    /// <summary>
    /// The caller reads the document: it holds <c>documents.read</c> (and <c>accounting.read</c> for a statement or an invoice) on the owner profile of the
    /// document and, for a document of a property, on that property.
    /// </summary>
    private static bool CanRead(OwnerPortalAccess access, OwnerDocument document)
    {
        var needed = document.Type is OwnerDocumentType.Statement or OwnerDocumentType.Invoice
            ? OwnerPortalPermission.DocumentsRead | OwnerPortalPermission.AccountingRead
            : OwnerPortalPermission.DocumentsRead;
        return access.Grants.Any(g => g.OrgId == document.OrgId
                                      && g.PropertyOwnerId == document.PropertyOwnerId
                                      && (document.PropertyId is null || g.PropertyId == document.PropertyId)
                                      && g.Permissions.HasFlag(needed));
    }

    /// <summary>The version in force on <paramref name="today"/>: the highest number already effective; the highest one when none is effective yet.</summary>
    private static OwnerDocumentVersion? CurrentOf(IReadOnlyCollection<OwnerDocumentVersion> versions, DateOnly today) =>
        versions.Where(v => v.EffectiveFrom is null || v.EffectiveFrom <= today).MaxBy(v => v.VersionNumber)
        ?? versions.MaxBy(v => v.VersionNumber);

    private async Task<OwnerDocumentVersion?> VersionOfAsync(Guid documentId, Guid? versionId, CancellationToken cancellationToken)
    {
        var versions = await Versions().AsNoTracking().Where(v => v.OwnerDocumentId == documentId).ToListAsync(cancellationToken);
        return versionId is { } wanted ? versions.FirstOrDefault(v => v.Id == wanted) : CurrentOf(versions, Today());
    }

    /// <summary>Writes the download (the audit) and hands the file out; a file missing from the storage is a 404 and writes nothing.</summary>
    private async Task<OwnerFileContent> HandOutAsync(
        OwnerDocument document,
        OwnerDocumentVersion version,
        Guid? ownerUserId,
        string? userId,
        CancellationToken cancellationToken)
    {
        var content = await storage.OpenReadAsync(StorageBucket.Private, version.StorageKey, cancellationToken);
        if (content is null)
        {
            logger.LogError("Owner document {DocumentId}: the file of version {VersionId} is missing from the storage", document.Id, version.Id);
            throw DocumentNotFound(document.Id);
        }

        try
        {
            db.OwnerDocumentDownloads.Add(new OwnerDocumentDownload
            {
                OrgId = document.OrgId,
                OwnerDocumentId = document.Id,
                OwnerDocumentVersionId = version.Id,
                DownloadedAt = Now(),
                OwnerUserId = ownerUserId,
                UserId = ownerUserId is null ? userId : null,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return new OwnerFileContent(content, version.FileName, version.ContentType);
    }

    private async Task<List<OwnerDocumentView>> DocumentViewsAsync(IReadOnlyCollection<OwnerDocument> documents, CancellationToken cancellationToken)
    {
        var ids = documents.Select(d => d.Id).ToList();
        var versions = await Versions().AsNoTracking().Where(v => ids.Contains(v.OwnerDocumentId)).ToListAsync(cancellationToken);
        var propertyIds = documents.Where(d => d.PropertyId != null).Select(d => d.PropertyId!.Value).Distinct().ToList();
        var names = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var today = Today();

        return documents.Select(document =>
        {
            var own = versions.Where(v => v.OwnerDocumentId == document.Id).OrderByDescending(v => v.VersionNumber).ToList();
            var current = CurrentOf(own, today);
            return new OwnerDocumentView(
                document.Id,
                document.OrgId,
                document.PropertyOwnerId,
                document.PropertyId,
                document.PropertyId is { } property ? names.GetValueOrDefault(property) : null,
                document.Type,
                document.Title,
                ExpiryStateOf(current, today),
                current?.ExpiresOn,
                own.Select(v => new OwnerDocumentVersionView(
                    v.Id, v.VersionNumber, v.FileName, v.ContentType, v.SizeBytes, v.Sha256, v.EffectiveFrom, v.ExpiresOn, StateOf(v, current, today), v.UploadedAt))
                    .ToList(),
                document.UpdatedAt);
        }).ToList();
    }

    private async Task<List<OwnerDocumentAdminView>> AdminViewsAsync(IReadOnlyCollection<OwnerDocument> documents, CancellationToken cancellationToken)
    {
        var views = await DocumentViewsAsync(documents, cancellationToken);
        var ids = documents.Select(d => d.Id).ToList();
        var downloads = await db.OwnerDocumentDownloads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => ids.Contains(d.OwnerDocumentId))
            .GroupBy(d => d.OwnerDocumentId)
            .Select(g => new { DocumentId = g.Key, Count = g.Count(), Last = g.Max(d => d.DownloadedAt) })
            .ToDictionaryAsync(g => g.DocumentId, cancellationToken);
        var owners = await OwnerNamesAsync(documents.Select(d => d.PropertyOwnerId), cancellationToken);
        var byId = documents.ToDictionary(d => d.Id);

        return views.Select(view =>
        {
            var document = byId[view.Id];
            var counted = downloads.GetValueOrDefault(view.Id);
            return new OwnerDocumentAdminView(
                view,
                owners.GetValueOrDefault(document.PropertyOwnerId, string.Empty),
                document.RevokedAt is null,
                document.RevokedAt,
                counted?.Count ?? 0,
                counted?.Last);
        }).ToList();
    }

    private static string StateOf(OwnerDocumentVersion version, OwnerDocumentVersion? current, DateOnly today)
    {
        if (version.EffectiveFrom is { } from && from > today)
            return "upcoming";
        if (current is not null && version.Id != current.Id)
            return "superseded";
        return version.ExpiresOn is { } expires && expires < today ? "expired" : "current";
    }

    private static string ExpiryStateOf(OwnerDocumentVersion? current, DateOnly today)
    {
        if (current?.ExpiresOn is not { } expires)
            return "none";
        if (expires < today)
            return "expired";
        return expires <= today.AddDays(OwnerCollaborationRules.ExpiryAlertDays) ? "expiring" : "valid";
    }

    /// <summary>The owner profile is of the org and not anonymized; the property, when named, is one it holds (or held) in the org.</summary>
    private async Task EnsureOwnerTargetAsync(Guid orgId, Guid propertyOwnerId, Guid? propertyId, CancellationToken cancellationToken)
    {
        var ownerOfOrg = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(o => o.Id == propertyOwnerId && o.OrgId == orgId && o.AnonymizedAt == null, cancellationToken);
        var propertyOfOwner = propertyId is not { } property
            || await db.PropertyOwnerships
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(o => o.OrgId == orgId && o.PropertyOwnerId == propertyOwnerId && o.PropertyId == property, cancellationToken);
        if (!ownerOfOrg || !propertyOfOwner)
        {
            throw new NotFoundException($"Owner {propertyOwnerId} not found")
            {
                Code = OwnerCollaborationErrors.DocumentOwnerNotFound,
                MessageKey = OwnerCollaborationErrors.DocumentOwnerNotFoundMessageKey,
            };
        }
    }

    /// <summary>The verdict of the scan on a clean file; a 422 with the code of the reason otherwise (the file is not stored).</summary>
    private OwnerFileVerdict Scan(OwnerFileUpload file)
    {
        var verdict = scanner.Scan(file.Content);
        if (!verdict.Clean)
        {
            var code = verdict.RejectionCode ?? OwnerFileRules.TypeNotAllowed;
            logger.LogWarning("An owner portal file was refused by the scan ({Engine}): {Code}", scanner.Engine, code);
            throw new DomainRuleException(code, OwnerFileRules.MessageKeyOf(code));
        }

        return verdict;
    }

    private OwnerDocumentVersion NewVersion(
        OwnerDocument document,
        int number,
        OwnerFileUpload file,
        OwnerFileVerdict verdict,
        DateOnly? effectiveFrom,
        DateOnly? expiresOn,
        string actorUserId,
        DateTime now)
    {
        var id = Guid.NewGuid();
        return new OwnerDocumentVersion
        {
            Id = id,
            OrgId = document.OrgId,
            OwnerDocumentId = document.Id,
            VersionNumber = number,
            StorageKey = StorageKeys.OwnerDocumentFile(document.OrgId, document.Id, $"{id:N}{verdict.Extension}"),
            FileName = OwnerFileRules.SafeFileName(file.FileName, verdict.Extension!, OwnerDocumentVersion.FileNameMaxLength),
            ContentType = verdict.ContentType!,
            SizeBytes = file.Content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
            EffectiveFrom = effectiveFrom,
            ExpiresOn = expiresOn,
            ScanEngine = scanner.Engine,
            UploadedAt = now,
            UploadedByUserId = actorUserId,
        };
    }

    private async Task StoreAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await storage.PutAsync(StorageBucket.Private, StorageKeys.EnsureValid(key), stream, contentType, cancellationToken);
    }

    /// <summary>Saves the unit of work; when the save fails, the file stored for it is removed again and the failure goes up.</summary>
    private async Task SaveOrForgetFileAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>The display names of owner profiles (never a fiscal or bank value of them).</summary>
    private async Task<Dictionary<Guid, string>> OwnerNamesAsync(IEnumerable<Guid> ownerIds, CancellationToken cancellationToken)
    {
        var ids = ownerIds.Distinct().ToList();
        var owners = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new { o.Id, o.FirstName, o.LastName, o.LegalName })
            .ToListAsync(cancellationToken);
        return owners.ToDictionary(
            o => o.Id,
            o => OwnerFieldRules.DisplayName(new PropertyOwner { FirstName = o.FirstName, LastName = o.LastName, LegalName = o.LegalName }));
    }

    /// <summary>The names of people of the portal (<c>OwnerUser</c>).</summary>
    private async Task<Dictionary<Guid, string>> OwnerUserNamesAsync(IEnumerable<Guid> ownerUserIds, CancellationToken cancellationToken)
    {
        var ids = ownerUserIds.Distinct().ToList();
        var people = await db.OwnerUsers.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken);
        return people.ToDictionary(p => p.Id, p => $"{p.FirstName} {p.LastName}".Trim());
    }

    /// <summary>Stages the line of the activity log of a change, in the unit of work of the change. Codes only.</summary>
    private void Audit(Guid orgId, Guid subjectId, string part, string status, string? actor) =>
        activityLog.Record(OrgActivity.Of(
            orgId,
            OrgActivityType.OwnerPortalChanged,
            actor,
            subjectId.ToString(),
            (OrgActivityDetailKeys.Part, part),
            (OrgActivityDetailKeys.Status, status)));

    private static void RequireDocument(bool valid, string field)
    {
        if (!valid)
            throw new DomainRuleException(OwnerCollaborationErrors.DocumentInvalid, OwnerCollaborationErrors.DocumentInvalidMessageKey, field);
    }

    private static NotFoundException DocumentNotFound(Guid documentId) =>
        new($"Owner document {documentId} not found")
        {
            Code = OwnerCollaborationErrors.DocumentNotFound,
            MessageKey = OwnerCollaborationErrors.DocumentNotFoundMessageKey,
        };
}
