using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class TerritorialRentAgreementRepository(AppDbContext context) : ITerritorialRentAgreementRepository
{
    public Task<TerritorialRentAgreement?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default)
    {
        var normalized = comune.Trim();
        return context.TerritorialRentAgreements
            .Include(a => a.Bands)
            .Include(a => a.Signatories)
            .FirstOrDefaultAsync(a => a.Comune.ToLower() == normalized.ToLower(), cancellationToken);
    }

    public async Task<IReadOnlyList<TerritorialRentAgreement>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await context.TerritorialRentAgreements
            .Include(a => a.Bands)
            .OrderBy(a => a.Comune)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<TerritorialRentAgreement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.TerritorialRentAgreements
            .Include(a => a.Bands)
            .Include(a => a.Signatories)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<ConcordatoRentBand?> GetBandByIdAsync(Guid agreementId, Guid bandId, CancellationToken cancellationToken = default) =>
        context.ConcordatoRentBands
            .FirstOrDefaultAsync(b => b.Id == bandId && b.TerritorialRentAgreementId == agreementId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}

public class HighTensionAreaComuneRepository(AppDbContext context) : IHighTensionAreaComuneRepository
{
    public Task<HighTensionAreaComune?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default)
    {
        var normalized = comune.Trim();
        return context.HighTensionAreaComuni
            .FirstOrDefaultAsync(c => c.Comune.ToLower() == normalized.ToLower(), cancellationToken);
    }
}

public class ComuneImuChannelRepository(AppDbContext context) : IComuneImuChannelRepository
{
    public Task<ComuneImuChannel?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default)
    {
        var normalized = comune.Trim();
        return context.ComuneImuChannels
            .FirstOrDefaultAsync(c => c.Comune.ToLower() == normalized.ToLower(), cancellationToken);
    }

    public async Task<IReadOnlyList<ComuneImuChannel>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await context.ComuneImuChannels
            .OrderBy(c => c.Comune)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<ComuneImuChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.ComuneImuChannels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}

public class RegulatoryDataAuditLogRepository(AppDbContext context) : IRegulatoryDataAuditLogRepository
{
    public async Task AddAsync(RegulatoryDataAuditEntry entry, CancellationToken cancellationToken = default)
    {
        context.RegulatoryDataAuditEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegulatoryDataAuditEntry>> GetByEntityIdAsync(
        Guid entityId, CancellationToken cancellationToken = default) =>
        await context.RegulatoryDataAuditEntries
            .Where(e => e.EntityId == entityId)
            .OrderByDescending(e => e.OccurredAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
}
