using System.Reflection;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the guest portal (epics E50-E60, casazen/backend#987) is registered: <c>Program.cs</c> calls <see cref="AddGuestPortal"/> and
/// <see cref="AddGuestPortalRegistries"/> once. Nothing here is conditional: what is not ready yet sits behind <c>Features:GuestPortal</c> (and its two
/// companion flags) where it is used, the endpoints, not by being left out of DI.
/// </summary>
public static class GuestPortalServiceCollectionExtensions
{
    /// <summary>The services of the guest portal.</summary>
    public static IServiceCollection AddGuestPortal(this IServiceCollection services)
    {
        // E50/F01/US01/T01 (NF-152): the stay link of a booking, made on request, regenerated and revoked by the host, resolved from its token.
        services.AddScoped<IGuestPortalLinkService, GuestPortalLinkService>();

        // E50/F01/US01/T02 (NF-152, NF-153): the context of a portal request, made from the token by the filter of GuestPortalControllerBase, and the
        // overview of the portal.
        services.AddScoped<PortalContextFactory>();
        services.AddScoped<PortalCheckInSessions>();
        services.AddScoped<GuestHouseGuideStorage>();
        services.AddScoped<GuestPortalSetupSuggestions>();
        services.AddScoped<GuestHostExtraService>();
        services.AddScoped<GuestSupplierOrders>();
        services.AddScoped<GuestSupplierCheckout>();
        services.AddScoped<GuestSupplierCaseFiles>();
        services.AddScoped<GuestSupplierPayouts>();
        services.AddScoped<GuestPortalHostNotifications>();
        services.AddScoped<GuestPortalInvoiceData>();
        services.AddScoped<GuestHostExtraTranslator>();
        services.AddScoped<GuestPortalRecovery>();
        services.AddScoped<GuestPortalContextAccessor>();
        services.AddScoped<IGuestPortalContextAccessor>(provider => provider.GetRequiredService<GuestPortalContextAccessor>());
        services.AddScoped<GuestPortalTokenFilter>();
        services.AddScoped<IGuestPortalOverviewService, GuestPortalOverviewService>();

        // E50/F01/US03/T03 (NF-152): the stay link in the e-mails and in the chat message of the check-in link (made by the link service above and nowhere else), and the host's «send it
        // again» of «Prepara il tuo arrivo». The services that send the messages take the provider as an optional dependency.
        services.AddScoped<IGuestStayLinkProvider, GuestStayLinkProvider>();
        services.AddScoped<IGuestPortalEmailService, GuestPortalEmailService>();

        return services;
    }

    /// <summary>
    /// The registries of the guest portal (decision A4): every concrete class of <paramref name="assemblies"/> that implements an interface marked
    /// <see cref="GuestPortalRegistryAttribute"/> (<see cref="IGuestPortalSection"/>, <see cref="IGuestPortalNextStep"/>,
    /// <see cref="IGuestPortalPolicy"/>, and the ones the later epics declare) is registered, scoped, under that interface. A new section, step or
    /// policy is a new class: no common file changes. Calling it again with the same assemblies registers nothing twice.
    /// </summary>
    public static IServiceCollection AddGuestPortalRegistries(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var scanned = assemblies.Distinct().ToList();
        var types = scanned.SelectMany(LoadableTypes).ToList();

        // The registries are the marked interfaces of the scanned assemblies and of Core, where the first ones live.
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
