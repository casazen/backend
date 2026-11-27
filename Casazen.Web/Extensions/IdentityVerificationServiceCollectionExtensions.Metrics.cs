using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The counters of the self-hosted identity verification (E73/F01/US03, NF-196, decision D10): the part of the hook <c>AddMetricsServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>). The service is registered always and runs only when an endpoint asks for it, and the metrics answer <c>identity</c>
/// only with <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c> on, so with the flags off the metrics of the operator queue are exactly the
/// previous ones.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddMetricsServices(IServiceCollection services)
    {
        // E73/F01/US03/T01: the counters with the complete denominator; scoped like the database it reads.
        services.AddScoped<IIdentityVerificationMetricsService, IdentityVerificationMetricsService>();
    }
}
