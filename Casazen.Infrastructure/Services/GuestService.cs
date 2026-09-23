using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class GuestService(IGuestRepository repository, ILogger<GuestService> logger) : IGuestService
{
    public async Task<Guest?> GetGuestAsync(Guid id)
    {
        logger.LogInformation("Retrieving guest: {GuestId}", id);
        return await repository.GetByIdAsync(id);
    }

    public async Task<Guest?> GetGuestByEmailAsync(string email)
    {
        logger.LogInformation("Retrieving guest by email lookup");
        return await repository.GetByEmailAsync(email);
    }

    public async Task<IEnumerable<Guest>> GetAllGuestsAsync()
    {
        logger.LogInformation("Retrieving all guests");
        return await repository.GetAllAsync();
    }

    public async Task<IEnumerable<Guest>> SearchGuestsAsync(string? searchTerm)
    {
        logger.LogInformation("Searching guests with term: {HasSearch}", !string.IsNullOrWhiteSpace(searchTerm));
        return await repository.SearchAsync(searchTerm);
    }

    public async Task<Guest> CreateGuestAsync(Guest guest)
    {
        logger.LogInformation("Creating guest");

        if (await repository.ExistsByEmailAsync(guest.Email))
        {
            logger.LogWarning("Guest with duplicate email already exists");
            throw new InvalidOperationException($"Guest with email {guest.Email} already exists");
        }

        var created = await repository.AddAsync(guest);
        logger.LogInformation("Guest created: {GuestId}", created.Id);
        return created;
    }

    public async Task<Guest> CreateGuestSnapshotAsync(Guest guest)
    {
        logger.LogInformation("Creating guest snapshot");
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
            throw new InvalidOperationException($"Guest with ID {guest.Id} not found");
        }

        var updated = await repository.UpdateAsync(guest);
        logger.LogInformation("Guest updated: {GuestId}", updated.Id);
        return updated;
    }

    public async Task<bool> DeleteGuestAsync(Guid id)
    {
        logger.LogInformation("Deleting guest: {GuestId}", id);

        if (!await repository.ExistsAsync(id))
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return false;
        }

        await repository.DeleteAsync(id);
        logger.LogInformation("Guest deleted: {GuestId}", id);
        return true;
    }
}
