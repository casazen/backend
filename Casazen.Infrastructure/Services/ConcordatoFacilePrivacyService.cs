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

/// <summary>
/// The privacy notice a landlord accepts for a lease before the guided contract form is opened (CF-BE). The acceptance is the lease event
/// <see cref="LeaseEventType.ConcordatoFacilePrivacyAccepted"/> with the payload <c>{ "policyVersion": ... }</c>: the version of the notice that
/// was current (<c>Legal:Documents:Privacy:Version</c>, <see cref="ILegalDocumentService"/>), nothing about the person. It counts for the
/// lease and for that version only: a new version of the notice asks for the acceptance again. The signed link
/// (<see cref="ConcordatoFacileLinkService"/>) is refused without it, for the platform admin too.
/// </summary>
public sealed class ConcordatoFacilePrivacyService(
    AppDbContext db,
    ILegalDocumentService legalDocuments,
    ILogger<ConcordatoFacilePrivacyService> logger,
    TimeProvider? timeProvider = null) : IConcordatoFacilePrivacyService
{
    /// <summary>409 code of the link asked before the privacy notice is accepted.</summary>
    public const string NotAcceptedCode = "concordatofacile_privacy_not_accepted";

    private const string NotAcceptedKey = "ConcordatoFacilePrivacyNotAccepted";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CfPrivacyState> GetStateAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        // The events hang off the lease and have no tenant of their own: the lease must be the org's before anything about it is read.
        var leaseOfTheOrg = await db.LeaseContracts.AnyAsync(l => l.Id == leaseId && l.OrgId == orgId, cancellationToken);
        return leaseOfTheOrg ? await ReadStateAsync(leaseId, cancellationToken) : CfPrivacyState.None;
    }

    public async Task<CfPrivacyState> AcceptAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        await db.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        // One request at a time per lease: a double click writes one event (the second one finds the first).
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

            // The lease and the version of the notice: no name, no address, no user.
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

    /// <summary>The acceptance of the current version (the earliest, there is one) and the last time the link was made, read without tracking.</summary>
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

    /// <summary>The payload of the event: the version of the notice, and nothing else.</summary>
    private sealed record AcceptancePayload([property: JsonPropertyName("policyVersion")] string? PolicyVersion);
}
