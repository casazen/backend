using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stub PDF/A generation. Real Docuengine call is blocked until counsel-reviewed templates exist.
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

        var placeholder = System.Text.Encoding.UTF8.GetBytes(
            $"[LEASE CONTRACT PDF PLACEHOLDER - LeaseId: {lease.Id} Regime: {lease.FiscalRegime} Version: {variant.VersionId}]");
        return Task.FromResult(placeholder);
    }
}
