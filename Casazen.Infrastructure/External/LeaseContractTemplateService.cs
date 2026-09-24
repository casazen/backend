using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Lease contract PDF from the template of the lease's fiscal regime (LT-03, A7-03). The final contract, the one sent
/// to signature and then to registration, exists only when the template is complete and approved by a lawyer and every
/// datum it uses is known; otherwise 422. The preview is always available and marked BOZZA.
/// </summary>
public class LeaseContractTemplateService(
    ILeaseContractTemplateCatalog catalog,
    ILogger<LeaseContractTemplateService> logger) : ILeaseTemplateService
{
    public const string TemplateNotApprovedCode = "contract_template_not_approved";
    public const string DataMissingCode = "contract_data_missing";

    public Task<byte[]> GeneratePdfAsync(LeaseContract lease)
    {
        var (template, data) = ResolveApprovedTemplate(lease);

        logger.LogInformation(
            "Generating final lease contract PDF. LeaseId={LeaseId} FiscalRegime={Regime} TemplateVersion={Version}",
            lease.Id, lease.FiscalRegime, template.VersionId);

        return Task.FromResult(FiscalPdfWriter.Write(template.Title!, LeaseContractDocument.BuildFinalBody(template, data)));
    }

    public void EnsureFinalContractAvailable(LeaseContract lease) => ResolveApprovedTemplate(lease);

    public string? GetFinalContractBlocker(LeaseContract lease)
    {
        var template = catalog.Get(lease.FiscalRegime);
        if (!template.IsApproved)
            return TemplateNotApprovedCode;

        return LeaseContractDocument.MissingData(template, LeaseContractDocument.ResolveData(lease)).Count > 0
            ? DataMissingCode
            : null;
    }

    private (LeaseContractTemplateState Template, IReadOnlyDictionary<string, string?> Data) ResolveApprovedTemplate(LeaseContract lease)
    {
        var template = catalog.Get(lease.FiscalRegime);
        if (!template.IsApproved)
        {
            logger.LogWarning(
                "Final lease contract blocked: template {Regime} is {Status} (version {VersionId}). LeaseId={LeaseId}",
                lease.FiscalRegime, template.Status, template.VersionId, lease.Id);
            throw new DomainRuleException(TemplateNotApprovedCode, "LeaseContractTemplateNotApproved");
        }

        var data = LeaseContractDocument.ResolveData(lease);
        var missing = LeaseContractDocument.MissingData(template, data);
        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Final lease contract blocked: data {MissingData} missing for template {Regime} (version {VersionId}). LeaseId={LeaseId}",
                string.Join(", ", missing), lease.FiscalRegime, template.VersionId, lease.Id);
            throw new DomainRuleException(DataMissingCode, "LeaseContractDataMissing", string.Join(", ", missing));
        }

        return (template, data);
    }

    public Task<byte[]> GeneratePreviewPdfAsync(LeaseContract lease)
    {
        var template = catalog.Get(lease.FiscalRegime);
        var marker = template.IsApproved ? LeaseContractDocument.ApprovedPreviewMarker : LeaseContractDocument.DraftMarker;
        var body = LeaseContractDocument.BuildPreviewBody(template, LeaseContractDocument.ResolveData(lease));

        logger.LogInformation(
            "Generating lease contract preview. LeaseId={LeaseId} FiscalRegime={Regime} TemplateStatus={Status}",
            lease.Id, lease.FiscalRegime, template.Status);

        return Task.FromResult(FiscalPdfWriter.Write(marker, $"{body}\n\n{marker}"));
    }
}
