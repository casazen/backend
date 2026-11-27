using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// What <see cref="AppDbContext"/> refuses to save (E06/F05/US02/T02, AC-NF110-1, AC-NF110-2): a change or a removal of a row that is evidence
/// (<see cref="IAppendOnlyRecord"/>, the owner ledger lines), the removal of a statement version, and any change of a published version other than its
/// move to superseded (or of a superseded one at all), and the removal or a change of the facts of a link to an external invoice (E06/F01/US02/T05).
/// The save throws 409 <c>owner_ledger_immutable</c> before anything is written.
/// </summary>
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

            // A link to an external invoice is never deleted and its facts never change: only its verification and its removal are recorded.
            if (entry.Entity is OwnerExternalDocumentReference
                && (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && !ExternalReferenceProperties.Contains(p.Metadata.Name))))
                throw Immutable();
        }
    }

    /// <summary>A published version may only become superseded; a superseded one never changes; the others follow their workflow.</summary>
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
