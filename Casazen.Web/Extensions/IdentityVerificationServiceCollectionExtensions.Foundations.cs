using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The foundations of the self-hosted identity verification (E70/F01/US02, NF-191, NF-193, NF-194): the versioned decision policy, the fingerprint of the
/// configuration a decision is bound to and the registry of the formats of the documents. This is the part of the hook <c>AddFoundationServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and read nothing until they are used, so with
/// <c>Features:CheckInIdentitySelfHosted</c> off nothing changes.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddFoundationServices(IServiceCollection services)
    {
        // The policy file is read once, at the first use (IdentityVerification:PolicyFile, pinned by IdentityVerification:PolicySha256).
        services.AddSingleton<IdentityPolicyProvider>();

        // The policy hash and the weight hashes of the models the store has loaded: null while any of them is missing.
        services.AddSingleton<IdentityConfigurationFingerprint>();

        // The formats of the documents: every file of IdentityVerification:FormatsDirectory, read once at the first use.
        services.AddSingleton<DocumentFormatRegistry>();
    }
}
