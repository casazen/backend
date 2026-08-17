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
