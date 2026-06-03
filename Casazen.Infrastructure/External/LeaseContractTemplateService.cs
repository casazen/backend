using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stub implementation — real PDF/A generation via Openapi.it template engine.
/// Replace with actual Docuengine API call when credentials are available.
/// </summary>
public class LeaseContractTemplateService(ILogger<LeaseContractTemplateService> logger) : ILeaseTemplateService
{
    public Task<byte[]> GeneratePdfAsync(LeaseContract lease)
    {
        logger.LogInformation("Generating PDF/A for LeaseId={LeaseId} FiscalRegime={Regime}",
            lease.Id, lease.FiscalRegime);

        // TODO: call Openapi.it Docuengine PDF generation endpoint
        // POST https://api.openapi.it/locazioni/contratto
        // Config: appsettings.json → Openapi:ApiKey
        var placeholder = System.Text.Encoding.UTF8.GetBytes($"[LEASE CONTRACT PDF PLACEHOLDER - LeaseId: {lease.Id}]");
        return Task.FromResult(placeholder);
    }
}
