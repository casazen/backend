using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

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
