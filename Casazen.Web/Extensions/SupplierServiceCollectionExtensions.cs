using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class SupplierServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierServices(this IServiceCollection services)
    {
        services.AddScoped<ISupplierService, Casazen.Infrastructure.Services.SupplierService>();
        services.AddScoped<ISupplierServiceCatalogService, SupplierServiceCatalogService>();
        services.AddScoped<ISupplierAgendaService, SupplierAgendaService>();
        services.AddScoped<ISupplierShowcaseContentService, SupplierShowcaseContentService>();
        services.AddScoped<IShowcaseTextSuggestionService, ShowcaseTextSuggestionService>();
        services.AddScoped<ISupplierShowcaseMediaService, SupplierShowcaseMediaService>();
        services.AddSingleton<PublicSupplierSlotCache>();
        services.AddScoped<IPublicSupplierShowcaseService, PublicSupplierShowcaseService>();
        services.AddScoped<ISupplierConflictService, SupplierConflictService>();
        services.AddScoped<ISupplierSlotPreviewService, SupplierSlotPreviewService>();
        services.AddScoped<IHostSupplierCatalogService, HostSupplierCatalogService>();
        services.AddScoped<ISupplierAdminService, SupplierAdminService>();
        services.AddOptions<Casazen.Core.Options.HostInviteOptions>()
            .BindConfiguration(Casazen.Core.Options.HostInviteOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.HostInviteOptions>, Casazen.Core.Options.HostInviteOptionsValidator>();
        services.AddScoped<IHostInviteService, HostInviteService>();
        services.AddScoped<IHostInviteRetentionService, HostInviteRetentionService>();

        return services;
    }
}
