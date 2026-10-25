using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services.CfSimulator;

/// <summary>
/// The status API of ConcordatoFacile as the test environment simulates it (CF-SIM): it serves the shapes of the specification v0.2 §4 (the
/// practices of a lease newest first and at most 20, a practice by its number with its <c>leaseId</c>) from the table
/// <c>cf_simulated_practices</c> instead of calling <c>https://stato.concordatofacile.it</c>. It is registered instead of the HTTP client
/// only with <c>ConcordatoFacile:FormMode=Simulated</c>. No key is asked: there is no outside service to authenticate to.
/// </summary>
public sealed class SimulatedConcordatoFacileApiClient(AppDbContext db) : IConcordatoFacileApiClient
{
    private const int MaxPracticesOfALease = 20;

    /// <summary>Always: the simulator reads a table of the application, there is no key to configure (CF-BE: the status cache asks).</summary>
    public bool IsConfigured => true;

    public async Task<List<PracticeDto>> GetPracticesByLeaseIdAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var practices = await db.CfSimulatedPractices
            .AsNoTracking()
            .Where(p => p.LeaseId == leaseId)
            .OrderByDescending(p => p.SubmittedAt)
            .Take(MaxPracticesOfALease)
            .ToListAsync(cancellationToken);

        // As in the list of the real API, a practice carries no leaseId: the list already is the lease's.
        return practices.Select(p => ToDto(p, withLease: false)).ToList();
    }

    public async Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(practiceId);

        // A number that is not one is not known, as the real API answers (404 practice_not_found).
        if (!CfSimulatorService.TryNormalizePracticeId(practiceId, out var number))
            return null;

        var practice = await db.CfSimulatedPractices.AsNoTracking().FirstOrDefaultAsync(p => p.PracticeId == number, cancellationToken);
        return practice is null ? null : ToDto(practice, withLease: true);
    }

    private static PracticeDto ToDto(CfSimulatedPractice practice, bool withLease) => new()
    {
        PracticeId = practice.PracticeId,
        Status = practice.Status,
        StatusChangedAt = practice.StatusChangedAt,
        SubmittedAt = practice.SubmittedAt,
        Comune = practice.Comune,
        ComuneDiffersFromLink = practice.ComuneDiffersFromLink(),
        CloseReason = practice.CloseReason,
        LeaseId = withLease ? practice.LeaseId : null,
    };
}
