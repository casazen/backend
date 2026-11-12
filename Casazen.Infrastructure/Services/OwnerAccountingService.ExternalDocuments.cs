using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>The links from the statement versions to the invoices issued by the agency's external fiscal system (E06/F01/US02/T05).</summary>
public sealed partial class OwnerAccountingService
{
    public async Task<OwnerExternalDocumentReference> AddExternalDocumentAsync(
        Guid orgId,
        Guid versionId,
        OwnerExternalDocumentCommand command,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var version = await db.OwnerStatementVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct)
            ?? throw VersionNotFound(versionId);
        if (version.Status is not (OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published))
            throw new DomainRuleException(OwnerAccountingErrors.VersionNotApproved, "OwnerStatementNotApproved");

        var provider = Truncate(command.Provider, int.MaxValue);
        var externalId = Truncate(command.ExternalId, int.MaxValue);
        var number = Truncate(command.ExternalNumber, int.MaxValue);
        var url = Truncate(command.DocumentUrl, int.MaxValue);
        var sha = Truncate(command.DocumentSha256, int.MaxValue)?.ToLowerInvariant();
        RequireExternal(provider is not null && provider.Length <= OwnerExternalDocumentReference.ProviderMaxLength, "provider");
        RequireExternal(externalId is not null && externalId.Length <= OwnerExternalDocumentReference.ExternalIdMaxLength, "externalId");
        RequireExternal(number is not null && number.Length <= OwnerExternalDocumentReference.NumberMaxLength, "externalNumber");
        RequireExternal(command.ExternalDate != default, "externalDate");
        RequireExternal(
            url is null || (url.Length <= OwnerExternalDocumentReference.UrlMaxLength
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps),
            "documentUrl");
        RequireExternal(sha is null || Sha256Pattern().IsMatch(sha), "documentSha256");

        var duplicate = await db.OwnerExternalDocumentReferences.AsNoTracking().AnyAsync(
            r => r.OrgId == orgId && r.OwnerStatementVersionId == versionId && r.Provider == provider && r.ExternalId == externalId && r.RemovedAt == null,
            ct);
        if (duplicate)
            throw new DomainConflictException(OwnerAccountingErrors.ExternalDocumentDuplicate, "OwnerExternalDocumentDuplicate");

        var reference = new OwnerExternalDocumentReference
        {
            OrgId = orgId,
            OwnerStatementVersionId = versionId,
            PropertyOwnerId = version.PropertyOwnerId,
            Provider = provider!,
            ExternalId = externalId!,
            ExternalNumber = number!,
            ExternalDate = command.ExternalDate,
            DocumentUrl = url,
            DocumentSha256 = sha,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            CreatedByUserId = userId,
        };
        db.OwnerExternalDocumentReferences.Add(reference);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "External document reference {ReferenceId} linked to owner statement version {VersionId} by {UserId}", reference.Id, versionId, userId);
        return reference;
    }

    public async Task<OwnerExternalDocumentReference> VerifyExternalDocumentAsync(Guid orgId, Guid referenceId, string? userId, CancellationToken ct = default)
    {
        var reference = await ExternalDocumentAsync(orgId, referenceId, ct);
        if (reference.RemovedAt is not null)
            throw new DomainConflictException(OwnerAccountingErrors.ExternalDocumentRemoved, "OwnerExternalDocumentRemoved");
        if (reference.Status == OwnerExternalDocumentStatus.Verified)
            return reference;
        reference.Status = OwnerExternalDocumentStatus.Verified;
        reference.VerifiedAt = time.GetUtcNow().UtcDateTime;
        reference.VerifiedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("External document reference {ReferenceId} verified by {UserId}", reference.Id, userId);
        return reference;
    }

    public async Task<OwnerExternalDocumentReference> RemoveExternalDocumentAsync(
        Guid orgId,
        Guid referenceId,
        string? reason,
        string? userId,
        CancellationToken ct = default)
    {
        var why = Truncate(reason, OwnerExternalDocumentReference.ReasonMaxLength);
        RequireExternal(why is not null, "reason");
        var reference = await ExternalDocumentAsync(orgId, referenceId, ct);
        if (reference.RemovedAt is not null)
            throw new DomainConflictException(OwnerAccountingErrors.ExternalDocumentRemoved, "OwnerExternalDocumentRemoved");
        reference.RemovedAt = time.GetUtcNow().UtcDateTime;
        reference.RemovedByUserId = userId;
        reference.RemovalReason = why;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("External document reference {ReferenceId} removed by {UserId}", reference.Id, userId);
        return reference;
    }

    public async Task<IReadOnlyList<OwnerExternalDocumentReference>> ListExternalDocumentsAsync(Guid orgId, Guid versionId, CancellationToken ct = default)
    {
        if (!await db.OwnerStatementVersions.AsNoTracking().AnyAsync(v => v.Id == versionId && v.OrgId == orgId, ct))
            throw VersionNotFound(versionId);
        return await db.OwnerExternalDocumentReferences.AsNoTracking()
            .Where(r => r.OrgId == orgId && r.OwnerStatementVersionId == versionId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OwnerExternalDocumentReference>> ListPublishedExternalDocumentsForOwnerUserAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var owners = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OwnerUserId == ownerUserId)
            .Select(o => new { o.OrgId, o.PropertyOwnerId })
            .Distinct()
            .ToListAsync(ct);
        var orgIds = owners.Select(o => o.OrgId).Distinct().ToList();
        var ownerIds = owners.Select(o => o.PropertyOwnerId).Distinct().ToList();
        var published = db.OwnerStatementVersions.AsNoTracking().Where(v => v.PublishedAt != null).Select(v => v.Id);
        var candidates = await db.OwnerExternalDocumentReferences.AsNoTracking()
            .Where(r => orgIds.Contains(r.OrgId) && ownerIds.Contains(r.PropertyOwnerId) && r.RemovedAt == null && published.Contains(r.OwnerStatementVersionId))
            .OrderByDescending(r => r.ExternalDate)
            .ToListAsync(ct);
        return candidates.Where(r => owners.Any(o => o.OrgId == r.OrgId && o.PropertyOwnerId == r.PropertyOwnerId)).ToList();
    }

    private async Task<OwnerExternalDocumentReference> ExternalDocumentAsync(Guid orgId, Guid referenceId, CancellationToken ct) =>
        await db.OwnerExternalDocumentReferences.FirstOrDefaultAsync(r => r.Id == referenceId && r.OrgId == orgId, ct)
        ?? throw new NotFoundException($"External document reference {referenceId} was not found")
        {
            Code = OwnerAccountingErrors.ExternalDocumentNotFound,
            MessageKey = "OwnerExternalDocumentNotFound",
        };

    private static void RequireExternal(bool valid, string field) =>
        Require(valid, OwnerAccountingErrors.ExternalDocumentInvalid, "OwnerExternalDocumentInvalid", field);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
