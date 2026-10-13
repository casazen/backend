using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// Lease contract templates (LT-03, A7-03): options validated at startup (a fake approval stops Production), catalog of
/// the template files and PDF service. Runbook: docs/runbooks/lease-contract-templates.md.
/// </summary>
public static class LeaseContractTemplateExtensions
{
    public static IServiceCollection AddCasazenLeaseContractTemplates(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LeaseTemplateOptions>()
            .Bind(configuration.GetSection(LeaseTemplateOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LeaseTemplateOptions>, LeaseTemplateOptionsValidator>();
        services.AddSingleton<ILeaseContractTemplateCatalog, LeaseContractTemplateCatalog>();
        services.AddScoped<ILeaseTemplateService, LeaseContractTemplateService>();
        return services;
    }
}
