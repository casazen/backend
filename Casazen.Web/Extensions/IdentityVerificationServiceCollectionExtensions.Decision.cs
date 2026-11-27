using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The decision of the self-hosted identity verification (E73/F01/US01, NF-193, NF-194, ADR-013-R18, R19): the engine of the versioned policy and the pipeline of the presence the
/// closure decides on. This is the part of the hook <c>AddDecisionServices</c> (see <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always
/// and decide nothing until the flow asks them, so with <c>Features:CheckInIdentitySelfHosted</c> off the check-in behaves as before. The closure itself is the part
/// <c>Sequence</c> of the flow service, already registered: it takes these services, the reader of the frames and the segments from the container.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDecisionServices(IServiceCollection services)
    {
        // E73/F01/US01/T01: the engine is pure (no dependency, no state, no clock, no logger, no randomness): one for the whole process.
        services.AddSingleton<IdentityDecisionEngine>();

        // E73/F01/US01/T02: the pipeline of the presence (validation, choice of the frames, liveness, match). Scoped like the liveness evaluator it holds; it keeps nothing of a frame.
        services.AddScoped<IPresenceEvidenceCollector, PresenceEvidenceCollector>();
    }
}
