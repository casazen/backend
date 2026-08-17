using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Draft PDF/A generation. Counsel-reviewed wording is still a BOZZA until templates are approved beyond dev-stub.
/// </summary>
public class LeaseContractTemplateService(
    IOptions<LeaseTemplateOptions> options,
    ILogger<LeaseContractTemplateService> logger) : ILeaseTemplateService
{
    public Task<byte[]> GeneratePdfAsync(LeaseContract lease)
    {
        var key = lease.FiscalRegime.ToString();
        if (!options.Value.Variants.TryGetValue(key, out var variant) || !variant.Approved)
        {
            throw new InvalidOperationException(
                $"No counsel-reviewed template is approved for fiscal regime {lease.FiscalRegime}.");
        }

        logger.LogInformation(
            "Generating PDF/A for LeaseId={LeaseId} FiscalRegime={Regime} TemplateVersion={Version}",
            lease.Id, lease.FiscalRegime, variant.VersionId);

        var isConcordato = lease.FiscalRegime == FiscalRegime.CanoneConcordato;
        var title = isConcordato
            ? "Contratto di locazione a canone concordato - BOZZA"
            : "Contratto di locazione - BOZZA";
        var body = isConcordato
            ? CanoneConcordatoContractBody.Build(lease, variant.VersionId)
            : CanoneConcordatoContractBody.BuildGenericDraft(lease, variant.VersionId);

        return Task.FromResult(FiscalPdfWriter.Write(title, body));
    }
}
