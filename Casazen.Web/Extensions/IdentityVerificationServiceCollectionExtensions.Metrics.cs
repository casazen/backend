using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The counters and the export of the pilot of the self-hosted identity verification (E73/F01/US03, NF-196, decision D10): the part of the hook <c>AddMetricsServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>). Both services are registered always and run only when an endpoint asks for them, and the endpoints answer
/// <c>identity</c> and the CSV only with <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c> on, so with the flags off the metrics of the
/// operator queue are exactly the previous ones and the export is a 404.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddMetricsServices(IServiceCollection services)
    {
        services.AddScoped<IIdentityVerificationMetricsService, IdentityVerificationMetricsService>();

        services.AddScoped<IdentityPilotExport>();
    }
}
