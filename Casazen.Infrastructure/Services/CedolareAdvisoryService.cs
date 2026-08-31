using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class CedolareAdvisoryService(
    ILeaseContractRepository leases,
    IHighTensionAreaComuneRepository ataComuni,
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
        var cedolareRate = await SelectCedolareRateAsync(lease, cfg, cancellationToken);

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

    private async Task<decimal> SelectCedolareRateAsync(
        LeaseContract lease,
        CedolareAdvisoryOptions cfg,
        CancellationToken cancellationToken)
    {
        if (lease.FiscalRegime != FiscalRegime.CanoneConcordato)
            return cfg.CedolareSeccaRate;

        var ata = await ataComuni.GetByComuneAsync(lease.Property.City, cancellationToken);
        return ata is { VerifiedDirectly: true }
            ? cfg.CanoneConcordatoRate
            : cfg.CedolareSeccaRate;
    }
}
