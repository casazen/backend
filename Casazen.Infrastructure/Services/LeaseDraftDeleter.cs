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
public sealed class LeaseDraftDeleter(
    AppDbContext db,
    IFileStorage storage,
    ILogger<LeaseDraftDeleter> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

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
    /// The one rule of what may be deleted, for the deletion the landlord asks for and for the retention job (CF-IMPORT): a draft with
    /// nothing that makes it a contract (<see cref="IsDeletable"/>) and without a ConcordatoFacile purchase that is open: pending (in its
    /// time), completed or refunded (ADR-004). A purchase that failed never charged anything and does not keep the draft, and neither does
    /// one still pending after its Checkout Session expired and a margin (<see cref="ConcordatoFacilePurchaseRules"/>, CF-BE): the
    /// customer did not come back, the session is gone, the purchase is closed. The rule looks at the purchases of the lease, not at the
    /// mark of the draft, so a draft made before the mark existed is protected as well.
    /// </summary>
    public async Task<bool> IsDeletableAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        if (!IsDeletable(lease))
            return false;

        var hasOpenPurchase = await db.Set<ConcordatoFacilePurchase>()
            .Where(p => p.LeaseContractId == lease.Id)
            .Where(ConcordatoFacilePurchaseRules.IsOpen(_clock.GetUtcNow().UtcDateTime))
            .AnyAsync(cancellationToken);

        return !hasOpenPurchase;
    }

    /// <summary>
    /// Removes the lease (loaded with <see cref="WithChildren"/>) and saves; returns the keys of its files in the private bucket,
    /// which the caller deletes after the commit with <see cref="DeleteFilesAsync"/>. 409 <c>lease_not_deletable</c> when the lease
    /// is not <see cref="IsDeletableAsync"/>, or when a record that refers to it (added after this code was written) stops the delete.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(LeaseContract lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!await IsDeletableAsync(lease, cancellationToken))
            throw new DomainConflictException(LeaseDraftErrorCodes.NotDeletable, "LeaseNotDeletable");

        var files = new[] { lease.QuesturaCommunicationReceiptPath, lease.Registration?.ReceiptStoragePath }
            .Where(StorageKeys.IsValid)
            .Select(key => key!)
            .ToList();

        // The documents the owner uploaded to the draft (NL-DOCS): the contract itself, with names, fiscal codes and addresses. The rows go
        // with the lease; the files in the private bucket would stay for ever if they were not listed here.
        var documents = await db.LeaseDocuments.Where(d => d.LeaseContractId == lease.Id).ToListAsync(cancellationToken);
        files.AddRange(documents.Select(d => d.StorageKey).Where(StorageKeys.IsValid));
        db.LeaseDocuments.RemoveRange(documents);

        var authorizations = await db.LeaseRegistrationAuthorizations
            .Where(a => a.LeaseContractId == lease.Id)
            .ToListAsync(cancellationToken);
        db.LeaseRegistrationAuthorizations.RemoveRange(authorizations);

        // CF-BE: the closed ConcordatoFacile purchases tied to the lease (checkouts that failed or expired, never paid) go with it. The key
        // from a purchase to its lease is Restrict, so on PostgreSQL they must be removed or the database would refuse the delete of a
        // draft the rule has just allowed. Only the closed ones: if a checkout opened a purchase after the rule was asked, it stays and
        // that key stops the delete (409) instead of erasing the record of a session that can still be paid.
        var isOpen = ConcordatoFacilePurchaseRules.IsOpen(_clock.GetUtcNow().UtcDateTime).Compile();
        var closedPurchases = (await db.Set<ConcordatoFacilePurchase>()
                .Where(p => p.LeaseContractId == lease.Id)
                .ToListAsync(cancellationToken))
            .Where(p => !isOpen(p))
            .ToList();
        db.Set<ConcordatoFacilePurchase>().RemoveRange(closedPurchases);

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
