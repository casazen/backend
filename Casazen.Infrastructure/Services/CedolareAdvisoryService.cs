using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class CedolareAdvisoryService(
    ILeaseContractRepository leases,
    IOptions<CedolareAdvisoryOptions> options) : ICedolareAdvisoryService
{
    public async Task<CedolareAdvisoryResult?> EvaluateAsync(
        Guid leaseId, string ownerId, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null || lease.Property.OwnerId != ownerId)
            return null;

        var cfg = options.Value;
        var annual = lease.MonthlyRent * 12m;
        var cedolareRate = lease.FiscalRegime == FiscalRegime.CanoneConcordato
            ? cfg.CanoneConcordatoRate
            : cfg.CedolareSeccaRate;

        return new CedolareAdvisoryResult(
            lease.FiscalRegime,
            annual,
            cedolareRate,
            decimal.Round(annual * cedolareRate, 2),
            cfg.RegistroRate,
            decimal.Round(annual * cfg.RegistroRate, 2),
            cfg.BolloEur,
            cfg.OrdinaryIrpefNote,
            cfg.Disclaimer);
    }
}
