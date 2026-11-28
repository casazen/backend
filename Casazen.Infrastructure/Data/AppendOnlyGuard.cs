using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Casazen.Infrastructure.Data;

internal static class AppendOnlyGuard
{
    private static readonly HashSet<string> SupersedingProperties =
    [
        nameof(OwnerStatementVersion.Status),
        nameof(OwnerStatementVersion.SupersededByVersionId),
        nameof(OwnerStatementVersion.SupersededAt),
    ];

    private static readonly HashSet<string> ExternalReferenceProperties =
    [
        nameof(OwnerExternalDocumentReference.Status),
        nameof(OwnerExternalDocumentReference.VerifiedAt),
        nameof(OwnerExternalDocumentReference.VerifiedByUserId),
        nameof(OwnerExternalDocumentReference.RemovedAt),
        nameof(OwnerExternalDocumentReference.RemovedByUserId),
        nameof(OwnerExternalDocumentReference.RemovalReason),
    ];

    public static void Check(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
                continue;

            if (entry.Entity is IAppendOnlyRecord || (entry.Entity is OwnerStatementVersion && entry.State == EntityState.Deleted))
                throw Immutable();

            if (entry.Entity is OwnerStatementVersion version && !MayChange(entry, version))
                throw Immutable();

            if (entry.Entity is OwnerExternalDocumentReference
                && (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && !ExternalReferenceProperties.Contains(p.Metadata.Name))))
                throw Immutable();
        }
    }

    private static bool MayChange(EntityEntry entry, OwnerStatementVersion version)
    {
        var original = (OwnerStatementVersionStatus)entry.Property(nameof(OwnerStatementVersion.Status)).OriginalValue!;
        return original switch
        {
            OwnerStatementVersionStatus.Superseded => !entry.Properties.Any(p => p.IsModified),
            OwnerStatementVersionStatus.Published => version.Status == OwnerStatementVersionStatus.Superseded
                && entry.Properties.Where(p => p.IsModified).All(p => SupersedingProperties.Contains(p.Metadata.Name)),
            _ => true,
        };
    }

    private static DomainConflictException Immutable() => new(OwnerAccountingErrors.LedgerImmutable, "OwnerLedgerImmutable");
}
