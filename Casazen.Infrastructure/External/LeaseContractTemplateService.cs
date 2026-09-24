using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Lease contract PDF from the template of the lease's fiscal regime (LT-03, A7-03). The final contract, the one sent
/// to signature and then to registration, exists only when the template is complete and approved by a lawyer and every
/// datum it uses is known; otherwise 422. The preview is always available, marked BOZZA and watermarked. Both are
/// rendered by <see cref="IPdfDocumentRenderer"/> (LT-09, A7-14): A4, wrapped and paginated, never truncated.
/// </summary>
public class LeaseContractTemplateService(
    ILeaseContractTemplateCatalog catalog,
    IPdfDocumentRenderer pdfRenderer,
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

        return Task.FromResult(pdfRenderer.Render(LeaseContractDocument.BuildFinalDocument(template, data)));
    }

    public void EnsureFinalContractAvailable(LeaseContract lease) => ResolveApprovedTemplate(lease);

    public string? GetFinalContractBlocker(LeaseContract lease)
    {
        var template = TemplateFor(lease);
        if (!template.IsApproved)
            return TemplateNotApprovedCode;

        return LeaseContractDocument.MissingData(template, LeaseContractDocument.ResolveData(lease)).Count > 0
            ? DataMissingCode
            : null;
    }

    private (LeaseContractTemplateState Template, IReadOnlyDictionary<string, string?> Data) ResolveApprovedTemplate(LeaseContract lease)
    {
        var template = TemplateFor(lease);
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

    /// <summary>
    /// Template of the lease: the one of its legacy fiscal regime, except for a transitorio lease (LT-10), which has no
    /// template yet (its clauses and transitory needs are not modelled): the regime's template would be a 4+4 contract.
    /// </summary>
    private LeaseContractTemplateState TemplateFor(LeaseContract lease) =>
        lease.ContractType == LeaseContractType.Transitorio
            ? new LeaseContractTemplateState(
                lease.FiscalRegime, null, LeaseContractTemplateStatus.Missing, null, [],
                LeaseContractTemplateStructure.RequiredSections(lease.FiscalRegime).Select(s => s.Id).ToList(),
                ["no template for transitorio leases"], false, [])
            : catalog.Get(lease.FiscalRegime);

    public Task<byte[]> GeneratePreviewPdfAsync(LeaseContract lease)
    {
        var template = TemplateFor(lease);
        var document = LeaseContractDocument.BuildPreviewDocument(template, LeaseContractDocument.ResolveData(lease));

        logger.LogInformation(
            "Generating lease contract preview. LeaseId={LeaseId} FiscalRegime={Regime} TemplateStatus={Status}",
            lease.Id, lease.FiscalRegime, template.Status);

        return Task.FromResult(pdfRenderer.Render(document));
    }
}
