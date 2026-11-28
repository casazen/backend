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
    TimeProvider? timeProvider = null, IDirectAlloggiatiRoute? directRoute = null) : IQuesturaCredentialsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<QuesturaCredentialsStatus> GetStatusAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
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

        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken,
            (PostgresAdvisoryLocks.Scope.InstitutionalSenderCutover, propertyId.ToString("N")));
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
                if (tx != null) await tx.CommitAsync(cancellationToken);
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

        if (!string.Equals(row.Username, credentials.Username.Trim(), StringComparison.OrdinalIgnoreCase) ||
            credentials.ApartmentId.HasValue && credentials.ApartmentId.Value != (int.TryParse(row.ApartmentId, out var currentApartment) ? currentApartment : 0))
            await EnsureCanRetireAccountAsync(row, cancellationToken);
        Apply(row, credentials, now);
        await db.SaveChangesAsync(cancellationToken);
        if (tx != null) await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Questura credentials of property {PropertyId} replaced", propertyId);
        return new QuesturaCredentialsStatus(true, row.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken,
            (PostgresAdvisoryLocks.Scope.InstitutionalSenderCutover, propertyId.ToString("N")));
        var current = await db.PropertyQuesturaCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (current == null) return false;
        await EnsureCanRetireAccountAsync(current, cancellationToken);
        var deleted = await db.PropertyQuesturaCredentials
            .Where(c => c.PropertyId == propertyId)
            .ExecuteDeleteAsync(cancellationToken);
        if (tx != null) await tx.CommitAsync(cancellationToken);
        if (deleted > 0)
            logger.LogInformation("Questura credentials of property {PropertyId} removed", propertyId);
        return deleted > 0;
    }

    private async Task EnsureCanRetireAccountAsync(PropertyQuesturaCredentials current, CancellationToken ct)
    {
        if (directRoute != null && await directRoute.EffectiveFromAsync(current.OrgId, current.PropertyId, ct) != null ||
            await db.AlloggiatiSendAttempts.AnyAsync(a => a.OrgId == current.OrgId && a.AccountKey == current.AccountKey &&
                a.Mode == AlloggiatiMode.Live && a.ReceiptId == null && (a.Status == AlloggiatiAttemptStatus.Accepted ||
                a.Status == AlloggiatiAttemptStatus.PartiallyAccepted || a.Status == AlloggiatiAttemptStatus.Sending ||
                a.Status == AlloggiatiAttemptStatus.ToVerify), ct))
            throw new Casazen.Core.Exceptions.DomainConflictException("alloggiati_account_in_use", "AlloggiatiAccountInUse");
    }

    private static void Apply(PropertyQuesturaCredentials row, QuesturaCredentialsInput credentials, DateTime now)
    {
        var user = credentials.Username.Trim();
        var accountChanged = !string.Equals(row.Username, user, StringComparison.OrdinalIgnoreCase);
        if (row.Username.Length > 0) row.CredentialVersion++;
        if (credentials.ApartmentId.HasValue)
        {
            row.Profile = credentials.ApartmentId > 0 ? Casazen.Core.Entities.Enums.AlloggiatiProfile.ApartmentsManagement
                : Casazen.Core.Entities.Enums.AlloggiatiProfile.SingleStructure;
            row.ApartmentId = credentials.ApartmentId > 0 ? credentials.ApartmentId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        if (accountChanged || credentials.ApartmentId.HasValue)
        {
            var identity = System.Text.Encoding.UTF8.GetBytes($"{row.OrgId:N}:{user.ToUpperInvariant()}");
            row.AccountKey = new Guid(System.Security.Cryptography.SHA256.HashData(identity).AsSpan(0, 16));
        }
        row.LastVerificationOk = null;
        row.LastVerificationCode = null;
        row.LastVerifiedAt = null;
        row.Username = user;
        row.Password = credentials.Password;
        row.WsKey = credentials.WsKey.Trim();
        row.UpdatedAt = now;
    }
}
