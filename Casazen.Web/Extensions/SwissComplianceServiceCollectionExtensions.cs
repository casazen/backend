using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The Swiss compliance files (Ticino notification, OTR tax, receipts), the assisted notification of the other cantons, the guest register and the
/// options of the guest notification.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class SwissComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSwissComplianceServices(this IServiceCollection services)
    {
        // NF-15: Swiss compliance files of the Ticino properties (ADR-016, E08): police notification XML, OTR overnight CSV and the
        // archive of the receipts the cantonal portal gives back. Used by SwissComplianceController.
        services.AddScoped<ITicinoNotificationService, TicinoNotificationService>();
        // E31/F02/US01/T01 (NF-131): the assisted notification of the guests to the authority of a canton other than Ticino (the days to notify, the guests as data ready to copy, the
        // declaration). The cockpit and the alerts read the days from it; the endpoints and the days are behind Features:GuestRegistrationRegimes.
        services.AddScoped<ISwissNotificationService, SwissNotificationService>();
        services.AddScoped<IOtrTaxService, OtrTaxService>();
        services.AddScoped<ISwissComplianceReceiptService, SwissComplianceReceiptService>();
        // E31/F01/US01/T01, NF-130 (casazen/backend#979): the guest register of a Swiss property for a period, a clean CSV table rebuilt on every request and never stored (one audit
        // row without personal data); both of its endpoints of SwissComplianceController are behind Features:GuestRegistrationRegimes.
        services.AddScoped<IGuestRegisterService, GuestRegisterService>();
        // NF-126 (casazen/backend#979): the character set of the CSV of the Ticino police, GuestNotifications:TicinoCsvEncoding (windows-1252 by default,
        // or utf-8); any other value stops the application at startup.
        services.AddOptions<Casazen.Core.Options.GuestNotificationOptions>()
            .BindConfiguration(Casazen.Core.Options.GuestNotificationOptions.SectionName)
            .Validate(
                options => Casazen.Core.Options.GuestNotificationOptions.TryParseTicinoCsvEncoding(options.TicinoCsvEncoding, out _),
                "GuestNotifications:TicinoCsvEncoding must be windows-1252 or utf-8 (docs/runbooks/guest-notification-switzerland.md).")
            .ValidateOnStart();

        return services;
    }
}
