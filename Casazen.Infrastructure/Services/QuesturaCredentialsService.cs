using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// The values go through the EF value converter of <see cref="AppDbContext"/> (encrypted at rest). One row per property
/// (unique index on <c>PropertyId</c>): a concurrent first save loses on 23505, re-reads the row and updates it. Logs name
/// the property only, never a value.
/// </remarks>
public sealed class QuesturaCredentialsService(
    AppDbContext db,
    ILogger<QuesturaCredentialsService> logger,
    TimeProvider? timeProvider = null) : IQuesturaCredentialsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<QuesturaCredentialsStatus> GetStatusAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        // Projection on the date only: the encrypted columns are never read (nor decrypted) for the status.
        var updatedAt = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .Where(c => c.PropertyId == propertyId)
            .Select(c => (DateTime?)c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return updatedAt is { } at ? new QuesturaCredentialsStatus(true, at) : QuesturaCredentialsStatus.NotConfigured;
    }

    public async Task<QuesturaCredentialsStatus> SetAsync(
        Guid propertyId,
        Guid orgId,
        QuesturaCredentialsInput credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var now = _clock.GetUtcNow().UtcDateTime;

        var row = await db.PropertyQuesturaCredentials
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (row is null)
        {
            row = new PropertyQuesturaCredentials { PropertyId = propertyId, OrgId = orgId, CreatedAt = now };
            Apply(row, credentials, now);
            db.PropertyQuesturaCredentials.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Questura credentials of property {PropertyId} configured", propertyId);
                return new QuesturaCredentialsStatus(true, row.UpdatedAt);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Created by a concurrent save: replace that row instead.
                db.Entry(row).State = EntityState.Detached;
                row = await db.PropertyQuesturaCredentials
                    .SingleAsync(c => c.PropertyId == propertyId, cancellationToken);
            }
        }

        Apply(row, credentials, now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Questura credentials of property {PropertyId} replaced", propertyId);
        return new QuesturaCredentialsStatus(true, row.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var deleted = await db.PropertyQuesturaCredentials
            .Where(c => c.PropertyId == propertyId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
            logger.LogInformation("Questura credentials of property {PropertyId} removed", propertyId);
        return deleted > 0;
    }

    private static void Apply(PropertyQuesturaCredentials row, QuesturaCredentialsInput credentials, DateTime now)
    {
        row.Username = credentials.Username.Trim();
        // The password is kept exactly as typed: spaces may be part of it.
        row.Password = credentials.Password;
        row.WsKey = credentials.WsKey.Trim();
        row.UpdatedAt = now;
    }
}
