using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class CedolareAdvisoryService(
    ILeaseContractRepository leases,
    IHighTensionAreaComuneRepository ataComuni,
    IOptions<CedolareAdvisoryOptions> options,
    TimeProvider clock) : ICedolareAdvisoryService
{
    public async Task<CedolareAdvisoryResult?> EvaluateAsync(
        Guid leaseId, CedolareAdvisoryInput? input = null, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease?.Property is null)
            return null;

        var ata = HighTensionArea.StatusOf(await ataComuni.GetByComuneAsync(lease.Property.City, cancellationToken));
        var facts = new LeaseTaxFacts(
            lease.FiscalRegime,
            lease.ContractType,
            lease.TaxRegime,
            lease.MonthlyRent,
            LeaseTerm.Between(lease.StartDate, lease.EndDate),
            ata,
            lease.HasExtraEUTenant);

        return LeaseTaxAdvisory.Compute(
            facts, input ?? CedolareAdvisoryInput.None, options.Value, clock.TodayInRome().Year);
    }
}
