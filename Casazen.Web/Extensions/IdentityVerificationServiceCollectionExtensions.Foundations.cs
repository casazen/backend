using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The foundations of the self-hosted identity verification (E70/F01/US02, NF-191, NF-193, NF-194): the versioned decision policy and the fingerprint of the
/// configuration a decision is bound to. This is the part of the hook <c>AddFoundationServices</c> (see <c>IdentityVerificationServiceCollectionExtensions.cs</c>);
/// the services are registered always and read nothing until they are used, so with <c>Features:CheckInIdentitySelfHosted</c> off nothing changes.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddFoundationServices(IServiceCollection services)
    {
        // The policy file is read once, at the first use (IdentityVerification:PolicyFile, pinned by IdentityVerification:PolicySha256).
        services.AddSingleton<IdentityPolicyProvider>();

        // The policy hash and the weight hashes of the models the store has loaded: null while any of them is missing.
        services.AddSingleton<IdentityConfigurationFingerprint>();
    }
}
