using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// The twin of <see cref="QuesturaCredentialsService"/> for the regional ISTAT credentials (E20/F03/US01/T01). The values go through the EF value
/// converter of <see cref="AppDbContext"/> (encrypted at rest, purpose <c>Casazen.PropertyIstatCredentials</c>). One row per property (unique index on
/// <c>PropertyId</c>): a concurrent first save loses on 23505, reads the row again and updates it. Logs name the property only, never a value.
/// </remarks>
public sealed class PropertyIstatCredentialsService(
    AppDbContext db,
    ILogger<PropertyIstatCredentialsService> logger,
    TimeProvider? timeProvider = null) : IPropertyIstatCredentialsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IstatCredentialsStatus> GetStatusAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        // Projection on the date only: the encrypted columns are never read (nor decrypted) for the status.
        var updatedAt = await db.PropertyIstatCredentials
            .AsNoTracking()
            .Where(c => c.PropertyId == propertyId)
            .Select(c => (DateTime?)c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return updatedAt is { } at ? new IstatCredentialsStatus(true, at) : IstatCredentialsStatus.NotConfigured;
    }

    public async Task<IstatCredentialsStatus> SetAsync(
        Guid propertyId,
        Guid orgId,
        IstatCredentialsInput credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var now = _clock.GetUtcNow().UtcDateTime;

        var row = await db.PropertyIstatCredentials.FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (row is null)
        {
            row = new PropertyIstatCredentials { PropertyId = propertyId, OrgId = orgId, CreatedAt = now };
            Apply(row, credentials, now);
            db.PropertyIstatCredentials.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("ISTAT credentials of property {PropertyId} configured", propertyId);
                return new IstatCredentialsStatus(true, row.UpdatedAt);
            }
            catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex))
            {
                // Created by a concurrent save: replace that row instead.
                db.Entry(row).State = EntityState.Detached;
                row = await db.PropertyIstatCredentials.SingleAsync(c => c.PropertyId == propertyId, cancellationToken);
            }
        }

        Apply(row, credentials, now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("ISTAT credentials of property {PropertyId} replaced", propertyId);
        return new IstatCredentialsStatus(true, row.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var row = await db.PropertyIstatCredentials.FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (row is null)
            return false;

        db.PropertyIstatCredentials.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("ISTAT credentials of property {PropertyId} removed", propertyId);
        return true;
    }

    private static void Apply(PropertyIstatCredentials row, IstatCredentialsInput credentials, DateTime now)
    {
        row.Username = credentials.Username.Trim();

        // The password is kept exactly as typed: spaces may be part of it.
        row.Password = credentials.Password;
        row.StructureCode = string.IsNullOrWhiteSpace(credentials.StructureCode) ? null : credentials.StructureCode.Trim();
        row.UpdatedAt = now;
    }
}
