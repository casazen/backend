using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Anonymization of the parties of long-term leases (LT-12, A7-18, #179; docs/runbooks/gdpr.md § 7). The reference date
/// is the lease's <see cref="LeaseContract.EndDate"/> (not the start date: a lease is in force until it ends), and a lease
/// has ended the calendar day (Europe/Rome) after it. The retention period comes from <c>Gdpr:Retention:LeaseParties</c>
/// with the same rules as the guest categories of CO-15 (<see cref="RetentionPeriodOptions"/>): without a period and a
/// cited source nothing is anonymized by retention, and every run says so. An erasure request is honoured from the day
/// after the end date.
/// </summary>
/// <remarks>
/// <para>A party whose fiscal code is also a party, not anonymized, of another lease of the same org that has not ended
/// is kept until that lease ends: the lease is retried at every run and marked anonymized only when every party is.</para>
/// <para>What is removed: names, fiscal code, citizenship and e-mail of the party, and the provider signing link and signer
/// id of its signature. What stays: role and extra-EU flag (shape of the lease, Questura history), dates and amounts of
/// the lease, the events (no personal data), and the stored documents (signed contract, RLI and Questura receipts): their
/// deletion is a decision of the product owner (runbook § 7).</para>
/// <para>The nightly run is a system job: the tenant filter is off, the queries span every org on purpose; the request
/// path runs with the caller's org filter. Every query on other leases is restricted explicitly to the lease's org.</para>
/// </remarks>
public sealed class LeasePartyPrivacyService(
    AppDbContext db,
    IOptions<GdprOptions> gdprOptions,
    TimeProvider timeProvider,
    ILogger<LeasePartyPrivacyService> logger) : ILeasePartyPrivacyService
{
    private const int ChunkSize = 100;

    /// <summary>Placeholder of the names and fiscal code of an anonymized party (same as the guests, CO-15).</summary>
    public const string AnonymizedValue = GuestDataEraser.AnonymizedName;

    /// <summary>The anonymized e-mail of a party: unique per row, never deliverable.</summary>
    public static string AnonymizedEmail(Guid partyId) => $"ANON-{partyId:N}@deleted.local";

    /// <summary>True from the calendar day after <paramref name="endDate"/> (the last day of the lease).</summary>
    public static bool HasEnded(DateTime endDate, DateTime todayInRome) => endDate.Date < todayInRome.Date;

    /// <summary>First day on which the lease has ended: the day after its end date.</summary>
    public static DateTime EndedFrom(DateTime endDate) =>
        DateTime.SpecifyKind(endDate.Date.AddDays(1), DateTimeKind.Utc);

    public async Task<LeaseErasureResult> RequestErasureAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .Include(l => l.Parties)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw new NotFoundException($"Lease {leaseId} not found.") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };

        var now = UtcNow();
        var today = timeProvider.TodayInRome();
        if (!lease.ErasureRequested)
        {
            lease.ErasureRequested = true;
            lease.ErasureRequestedAt = now;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.ErasureRequested,
                OccurredAt = now,
            });
        }

        LeaseErasureResult result;
        if (lease.PartiesAnonymizedAt is not null)
        {
            result = new LeaseErasureResult(LeaseErasureStatus.Anonymized, null, 0);
        }
        else if (!HasEnded(lease.EndDate, today))
        {
            result = new LeaseErasureResult(LeaseErasureStatus.Scheduled, EndedFrom(lease.EndDate), 0);
        }
        else
        {
            var (_, kept) = await AnonymizePartiesAsync(lease, today, now, cancellationToken);
            result = new LeaseErasureResult(
                kept == 0 ? LeaseErasureStatus.Anonymized : LeaseErasureStatus.PartiallyAnonymized, null, kept);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Lease party erasure requested. LeaseId={LeaseId} Status={Status} PartiesKept={PartiesKept}",
            lease.Id, result.Status, result.PartiesKept);
        return result;
    }

    public async Task<LeasePartyRetentionRunResult> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        var period = gdprOptions.Value.Retention.LeaseParties;
        var configured = period.IsConfigured;
        if (!configured)
        {
            logger.LogWarning(
                "GDPR retention: lease parties not applied ({Problem}); only the ended leases with an erasure request are " +
                "anonymized until Gdpr:Retention:LeaseParties has a period and its source (docs/runbooks/gdpr.md)",
                period.ConfigurationProblem);
        }

        var today = timeProvider.TodayInRome();
        var candidateEndBefore = configured ? period.CandidateStartBefore(today) : DateTime.MinValue;

        // Coarse filter in SQL, exact check in memory (month ends, RetentionPeriodOptions.CandidateStartBefore).
        var candidates = await db.LeaseContracts
            .Where(l => l.PartiesAnonymizedAt == null
                && ((l.ErasureRequested && l.EndDate < today) || (configured && l.EndDate < candidateEndBefore)))
            .Select(l => new { l.Id, l.EndDate, l.ErasureRequested })
            .ToListAsync(cancellationToken);
        var dueIds = candidates
            .Where(c => (c.ErasureRequested && HasEnded(c.EndDate, today)) || (configured && period.HasEnded(c.EndDate, today)))
            .Select(c => c.Id)
            .ToList();

        int leases = 0, parties = 0, kept = 0;
        foreach (var chunk in dueIds.Chunk(ChunkSize))
        {
            var now = UtcNow();
            var loaded = await db.LeaseContracts
                .Include(l => l.Parties)
                .Where(l => chunk.Contains(l.Id))
                .ToListAsync(cancellationToken);
            foreach (var lease in loaded)
            {
                var (anonymized, leaseKept) = await AnonymizePartiesAsync(lease, today, now, cancellationToken);
                parties += anonymized;
                kept += leaseKept;
                if (lease.PartiesAnonymizedAt is not null)
                    leases++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "GDPR retention: lease parties {Mode}: {Leases} leases, {Parties} parties anonymized, {Kept} parties kept for leases not ended",
            configured ? "applied" : "erasure requests only", leases, parties, kept);
        return new LeasePartyRetentionRunResult(configured, leases, parties, kept);
    }

    /// <summary>
    /// Anonymizes the parties of <paramref name="lease"/> (loaded with its parties) that are not parties of another lease
    /// of the same org that has not ended; marks the lease when none is left. Stages the changes without saving.
    /// </summary>
    private async Task<(int Anonymized, int Kept)> AnonymizePartiesAsync(
        LeaseContract lease,
        DateTime today,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var pending = lease.Parties.Where(p => p.AnonymizedAt is null).ToList();
        var codes = pending.Select(p => NormalizeFiscalCode(p.FiscalCode)).Where(c => c.Length > 0).Distinct().ToList();

        // Same person (fiscal code) in another lease of the org still in force: keep the data until that one ends too.
        var activeCodes = codes.Count == 0
            ? []
            : await db.Parties
                .Where(p => p.LeaseContractId != lease.Id
                    && p.AnonymizedAt == null
                    && p.LeaseContract.OrgId == lease.OrgId
                    && p.LeaseContract.EndDate >= today
                    && codes.Contains(p.FiscalCode.Trim().ToUpper()))
                .Select(p => p.FiscalCode.Trim().ToUpper())
                .Distinct()
                .ToListAsync(cancellationToken);
        var activeSet = activeCodes.ToHashSet(StringComparer.Ordinal);

        var anonymizedIds = new List<Guid>();
        foreach (var party in pending)
        {
            if (activeSet.Contains(NormalizeFiscalCode(party.FiscalCode)))
                continue;

            AnonymizeParty(party, now);
            anonymizedIds.Add(party.Id);
        }

        if (anonymizedIds.Count > 0)
        {
            var signers = await db.LeaseSigners
                .Where(s => s.LeaseContractId == lease.Id && anonymizedIds.Contains(s.PartyId))
                .ToListAsync(cancellationToken);
            foreach (var signer in signers)
            {
                signer.SigningUrl = null;
                signer.SigningUrlExpiresAt = null;
                signer.ExternalSignerId = null;
                signer.UpdatedAt = now;
            }
        }

        var kept = pending.Count - anonymizedIds.Count;
        if (kept == 0)
            lease.PartiesAnonymizedAt = now;
        if (anonymizedIds.Count > 0 || kept == 0)
            lease.UpdatedAt = now;

        if (kept > 0)
        {
            logger.LogInformation(
                "Lease parties kept: party of another lease not ended. LeaseId={LeaseId} PartiesKept={PartiesKept}",
                lease.Id, kept);
        }

        return (anonymizedIds.Count, kept);
    }

    /// <summary>Replaces every personal field of <paramref name="party"/>; role and extra-EU flag stay.</summary>
    public static void AnonymizeParty(Party party, DateTime now)
    {
        party.FirstName = AnonymizedValue;
        party.LastName = AnonymizedValue;
        party.FiscalCode = AnonymizedValue;
        party.Citizenship = string.Empty;
        party.ContactEmail = AnonymizedEmail(party.Id);
        party.AnonymizedAt ??= now;
    }

    private static string NormalizeFiscalCode(string? fiscalCode) => (fiscalCode ?? string.Empty).Trim().ToUpperInvariant();

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
