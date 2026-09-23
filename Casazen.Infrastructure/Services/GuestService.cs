using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class GuestService(
    IGuestRepository repository,
    IGdprService gdprService,
    TimeProvider timeProvider,
    ILogger<GuestService> logger) : IGuestService
{
    public const string HostDeletionReason = "Deleted by the host from the guest list";

    public async Task<Guest?> GetGuestAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Retrieving guest: {GuestId}", id);
        return await repository.GetByIdInOrgAsync(orgId, id, cancellationToken);
    }

    public async Task<Guest?> GetGuestByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Retrieving guest by email lookup");
        return await repository.GetByEmailAsync(orgId, email, cancellationToken);
    }

    public async Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetGuestsPageAsync(
        Guid orgId,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Listing guests of org {OrgId}: page {Page}, size {PageSize}, search {HasSearch}",
            orgId, page, pageSize, !string.IsNullOrWhiteSpace(searchTerm));
        return await repository.GetPageAsync(orgId, searchTerm, page, pageSize, cancellationToken);
    }

    public async Task<Guest> CreateGuestAsync(Guid orgId, Guest guest, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating guest in org {OrgId}", orgId);

        // Only the caller's org is checked: a guest with the same e-mail in another org is a different
        // record and must never be revealed (no cross-tenant 409).
        if (await repository.ExistsByEmailAsync(orgId, guest.Email, cancellationToken))
        {
            logger.LogWarning("Guest with duplicate email already exists in org {OrgId}", orgId);
            throw new DomainConflictException("guest_email_exists", "GuestEmailAlreadyExists");
        }

        guest.OrgId = orgId;
        var created = await repository.AddAsync(guest);
        logger.LogInformation("Guest created: {GuestId}", created.Id);
        return created;
    }

    public async Task<Guest> CreateGuestSnapshotAsync(Guest guest)
    {
        if (guest.OrgId == Guid.Empty)
            throw new ArgumentException("A guest snapshot must belong to the booking's org.", nameof(guest));

        logger.LogInformation("Creating guest snapshot in org {OrgId}", guest.OrgId);
        var created = await repository.AddAsync(guest);
        logger.LogInformation("Guest snapshot created: {GuestId}", created.Id);
        return created;
    }

    public async Task<Guest> UpdateGuestAsync(Guest guest)
    {
        logger.LogInformation("Updating guest: {GuestId}", guest.Id);

        if (!await repository.ExistsAsync(guest.Id))
        {
            logger.LogWarning("Guest not found: {GuestId}", guest.Id);
            throw GuestNotFound(guest.Id);
        }

        var updated = await repository.UpdateAsync(guest);
        logger.LogInformation("Guest updated: {GuestId}", updated.Id);
        return updated;
    }

    public async Task<GuestDeletionResult> DeleteGuestAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Deleting guest: {GuestId}", id);

        var guest = await repository.GetByIdInOrgAsync(orgId, id, cancellationToken);
        if (guest is null)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            throw GuestNotFound(id);
        }

        var usage = await repository.GetUsageAsync(id, timeProvider.TodayInRome(), cancellationToken);
        if (usage.HasOpenBookings)
        {
            logger.LogWarning("Guest {GuestId} not deleted: it has open bookings", id);
            throw new DomainConflictException("guest_has_open_bookings", "GuestHasOpenBookings");
        }

        if (!usage.HasReferences)
        {
            await repository.DeleteAsync(id);
            logger.LogInformation("Guest deleted: {GuestId}", id);
            return GuestDeletionResult.Deleted;
        }

        // Bookings and Alloggiati reports keep their Restrict FK to the guest: the row stays, marked
        // deleted and anonymized like a GDPR erasure (detailed retention rules: CO-15).
        await gdprService.DeleteGuestDataAsync(orgId, id, HostDeletionReason);
        logger.LogInformation("Guest {GuestId} soft-deleted and anonymized: it is referenced by bookings", id);
        return GuestDeletionResult.Anonymized;
    }

    private static NotFoundException GuestNotFound(Guid guestId) =>
        new($"Guest {guestId} not found") { Code = "guest_not_found", MessageKey = "GuestNotFound" };
}
