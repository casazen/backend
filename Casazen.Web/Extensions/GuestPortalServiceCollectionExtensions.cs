using System.Reflection;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

public static class GuestPortalServiceCollectionExtensions
{
    public static IServiceCollection AddGuestPortal(this IServiceCollection services)
    {
        services.AddScoped<IGuestPortalLinkService, GuestPortalLinkService>();

        services.AddScoped<PortalContextFactory>();
        services.AddScoped<PortalCheckInSessions>();
        services.AddScoped<GuestHouseGuideStorage>();
        services.AddScoped<GuestPortalSetupSuggestions>();
        services.AddScoped<GuestHostExtraService>();
        services.AddScoped<GuestSupplierOrders>();
        services.AddScoped<GuestSupplierDocuments>();
        services.AddScoped<GuestSupplierCheckout>();
        services.AddScoped<GuestSupplierCaseFiles>();
        services.AddScoped<GuestSupplierPayouts>();
        services.AddScoped<GuestPortalHostNotifications>();
        services.AddScoped<GuestPortalInvoiceData>();
        services.AddScoped<GuestHostExtraTranslator>();
        services.AddScoped<GuestGuideTranslator>();
        services.AddScoped<GuestPortalRecovery>();
        services.AddScoped<GuestPortalContextAccessor>();
        services.AddScoped<IGuestPortalContextAccessor>(provider => provider.GetRequiredService<GuestPortalContextAccessor>());
        services.AddScoped<GuestPortalTokenFilter>();
        services.AddScoped<IGuestPortalOverviewService, GuestPortalOverviewService>();

        services.AddScoped<IGuestStayLinkProvider, GuestStayLinkProvider>();
        services.AddScoped<IGuestPortalEmailService, GuestPortalEmailService>();

        return services;
    }

    public static IServiceCollection AddGuestPortalRegistries(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var scanned = assemblies.Distinct().ToList();
        var types = scanned.SelectMany(LoadableTypes).ToList();

        var registries = scanned
            .Append(typeof(GuestPortalRegistryAttribute).Assembly)
            .Distinct()
            .SelectMany(LoadableTypes)
            .Where(t => t.IsInterface && t.IsDefined(typeof(GuestPortalRegistryAttribute), inherit: false))
            .ToList();

        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            foreach (var registry in registries.Where(r => r.IsAssignableFrom(type)))
                services.TryAddEnumerable(ServiceDescriptor.Scoped(registry, type));
        }

        return services;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
