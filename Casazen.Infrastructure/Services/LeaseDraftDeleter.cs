using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Deletes a lease draft with everything tied to it (LR-12): its parties, signers, events, registration and authorizations, and the
/// files in the private bucket. The one place that knows what a draft is made of, used by the deletion the landlord asks for
/// (<see cref="LeaseDraftService"/>) and by the retention job (<see cref="LeaseDraftRetentionService"/>), so they cannot disagree
/// about what may be deleted.
/// </summary>
public sealed class LeaseDraftDeleter(AppDbContext db, IFileStorage storage, ILogger<LeaseDraftDeleter> logger)
{
    /// <summary>The lease with every child the delete has to take with it (EF cascades what is loaded; the database cascades the rest).</summary>
    public IQueryable<LeaseContract> WithChildren() =>
        db.LeaseContracts
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Include(l => l.Signers)
            .Include(l => l.Registration)
            .Include(l => l.RentSchedule);

    /// <summary>A draft with nothing that makes it a contract: not a draft any more, a signed copy, or a rent plan keep it.</summary>
    public static bool IsDeletable(LeaseContract lease) =>
        lease.Status == LeaseStatus.Draft
        && string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath)
        && lease.RentSchedule is null;

    /// <summary>
    /// Async version that checks CF purchase protection (ADR-004): a CF draft with a pending or completed purchase is not deletable.
    /// A draft with nothing that makes it a contract: not a draft any more, a signed copy, a rent plan, or a CF purchase keep it.
    /// </summary>
    public async Task<bool> IsDeletableAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        if (!IsDeletable(lease))
            return false;

        // CF drafts with associated purchases (non-failed) are protected from deletion
        if (lease.IsConcordatoFacileSource)
        {
            var hasPurchase = await db.Set<ConcordatoFacilePurchase>()
                .AnyAsync(p => p.LeaseContractId == lease.Id
                    && p.Status != ConcordatoFacilePurchaseStatus.Failed,
                    cancellationToken);

            if (hasPurchase)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Removes the lease (loaded with <see cref="WithChildren"/>) and saves; returns the keys of its files in the private bucket,
    /// which the caller deletes after the commit with <see cref="DeleteFilesAsync"/>. 409 <c>lease_not_deletable</c> when the lease
    /// is not <see cref="IsDeletable"/>, or when a record that refers to it (added after this code was written) stops the delete.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(LeaseContract lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsDeletable(lease))
            throw new DomainConflictException(LeaseDraftErrorCodes.NotDeletable, "LeaseNotDeletable");

        var files = new[] { lease.QuesturaCommunicationReceiptPath, lease.Registration?.ReceiptStoragePath }
            .Where(StorageKeys.IsValid)
            .Select(key => key!)
            .ToList();

        var authorizations = await db.LeaseRegistrationAuthorizations
            .Where(a => a.LeaseContractId == lease.Id)
            .ToListAsync(cancellationToken);
        db.LeaseRegistrationAuthorizations.RemoveRange(authorizations);
        db.LeaseContracts.Remove(lease);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Lease draft could not be deleted: a record still refers to it. LeaseId={LeaseId}", lease.Id);
            throw new DomainConflictException(LeaseDraftErrorCodes.NotDeletable, "LeaseNotDeletable");
        }

        return files;
    }

    /// <summary>Deletes the files (a missing one is not an error); one that cannot be deleted is logged without any personal data and left.</summary>
    public async Task DeleteFilesAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A file of a deleted lease draft could not be removed from the private bucket");
            }
        }
    }
}
