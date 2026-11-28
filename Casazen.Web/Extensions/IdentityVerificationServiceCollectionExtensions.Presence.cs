using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The presence of the self-hosted identity verification (E72/F01/US01, NF-192, ADR-013-R17, R22): the challenge the server issues, the intake of the frames the
/// browser sends and the validation of the sequence. This is the part of the hook <c>AddPresenceServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and read <c>Features:CheckInIdentitySelfHosted</c> (and
/// <c>CheckInIdentityVerification</c>) when they run, so with the flags off the check-in behaves as before.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddPresenceServices(IServiceCollection services)
    {
        services.AddSingleton<FrameSequenceReader>();

        services.AddSingleton<PresenceChallengeIssuer>();
        services.AddScoped<IIdentityPresenceFlow>(provider => (IIdentityPresenceFlow)provider.GetRequiredService<IIdentityVerificationFlowService>());

        services.AddSingleton<PresenceSequenceValidator>();
    }
}
