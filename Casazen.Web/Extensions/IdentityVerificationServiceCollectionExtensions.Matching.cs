using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The matching of the self-hosted identity verification (E72/F01/US02, NF-192, NF-193, ADR-013-R17, R19): the server chooses the frames of the validated presence sequence, judges
/// their liveness and compares them with the portrait of the document. This is the part of the hook <c>AddMatchingServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and do nothing until the flow asks them, so with
/// <c>Features:CheckInIdentitySelfHosted</c> off the check-in behaves as before.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddMatchingServices(IServiceCollection services)
    {
        // E72/F01/US02/T01: the choice of the frames (stateless: the draw is RandomNumberGenerator) and the liveness of the frames it chose. The evaluator is scoped because the
        // anti-spoofing service of the check-in is.
        services.AddSingleton<PresenceFrameSelector>();
        services.AddScoped<SequenceLivenessEvaluator>();
    }
}
