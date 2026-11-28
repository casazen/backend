using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed class ConcordatoFacilePrivacyService(
    AppDbContext db,
    ILegalDocumentService legalDocuments,
    ILogger<ConcordatoFacilePrivacyService> logger,
    TimeProvider? timeProvider = null) : IConcordatoFacilePrivacyService
{
    public const string NotAcceptedCode = "concordatofacile_privacy_not_accepted";

    private const string NotAcceptedKey = "ConcordatoFacilePrivacyNotAccepted";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CfPrivacyState> GetStateAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        var leaseOfTheOrg = await db.LeaseContracts.AnyAsync(l => l.Id == leaseId && l.OrgId == orgId, cancellationToken);
        return leaseOfTheOrg ? await ReadStateAsync(leaseId, cancellationToken) : CfPrivacyState.None;
    }

    public async Task<CfPrivacyState> AcceptAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        await db.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.ConcordatoFacilePrivacy, leaseId.ToString("N")));

        var state = await ReadStateAsync(leaseId, cancellationToken);
        if (!state.Accepted)
        {
            var version = CurrentVersion();
            var now = _clock.GetUtcNow().UtcDateTime;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = leaseId,
                EventType = LeaseEventType.ConcordatoFacilePrivacyAccepted,
                OccurredAt = now,
                Payload = JsonSerializer.Serialize(new AcceptancePayload(version)),
            });
            await db.SaveChangesAsync(cancellationToken);
            state = state with { Accepted = true, AcceptedAt = now };

            logger.LogInformation(
                "Privacy notice {PolicyVersion} accepted for the guided contract of lease {LeaseId}",
                version,
                leaseId);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return state;
    }

    public async Task EnsureAcceptedAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(leaseId, cancellationToken);
        if (!state.Accepted)
        {
            logger.LogInformation(
                "Signed link refused for lease {LeaseId}: the current privacy notice is not accepted",
                leaseId);
            throw new DomainConflictException(NotAcceptedCode, NotAcceptedKey);
        }
    }

    private async Task<CfPrivacyState> ReadStateAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var events = await db.LeaseEvents
            .AsNoTracking()
            .Where(e => e.LeaseContractId == leaseId
                && (e.EventType == LeaseEventType.ConcordatoFacilePrivacyAccepted
                    || e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated))
            .Select(e => new { e.EventType, e.OccurredAt, e.Payload })
            .ToListAsync(cancellationToken);

        var version = CurrentVersion();
        DateTime? acceptedAt = events
            .Where(e => e.EventType == LeaseEventType.ConcordatoFacilePrivacyAccepted
                && string.Equals(VersionOf(e.Payload), version, StringComparison.Ordinal))
            .Select(e => (DateTime?)e.OccurredAt)
            .Min();
        DateTime? linkOpenedAt = events
            .Where(e => e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated)
            .Select(e => (DateTime?)e.OccurredAt)
            .Max();

        return new CfPrivacyState(acceptedAt is not null, acceptedAt, linkOpenedAt);
    }

    private string CurrentVersion() => legalDocuments.GetPrivacy().Version;

    private static string? VersionOf(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AcceptancePayload>(payload)?.PolicyVersion;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record AcceptancePayload([property: JsonPropertyName("policyVersion")] string? PolicyVersion);
}
