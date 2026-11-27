using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class SwissComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSwissComplianceServices(this IServiceCollection services)
    {
        services.AddScoped<ITicinoNotificationService, TicinoNotificationService>();
        services.AddScoped<ISwissNotificationService, SwissNotificationService>();
        services.AddScoped<IOtrTaxService, OtrTaxService>();
        services.AddScoped<ISwissComplianceReceiptService, SwissComplianceReceiptService>();
        // E31/F01/US01/T01, NF-130 (casazen/backend#979): the guest register of a Swiss property for a period, a clean CSV table rebuilt on every request and never stored (one audit
        // row without personal data); both of its endpoints of SwissComplianceController are behind Features:GuestRegistrationRegimes.
        services.AddScoped<IGuestRegisterService, GuestRegisterService>();
        services.AddOptions<Casazen.Core.Options.GuestNotificationOptions>()
            .BindConfiguration(Casazen.Core.Options.GuestNotificationOptions.SectionName)
            .Validate(
                options => Casazen.Core.Options.GuestNotificationOptions.TryParseTicinoCsvEncoding(options.TicinoCsvEncoding, out _),
                "GuestNotifications:TicinoCsvEncoding must be windows-1252 or utf-8 (docs/runbooks/guest-notification-switzerland.md).")
            .ValidateOnStart();

        return services;
    }
}
