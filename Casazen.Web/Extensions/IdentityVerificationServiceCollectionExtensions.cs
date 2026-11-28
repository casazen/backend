using Casazen.Core.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The registrations of the self-hosted identity verification of the check-in (E70-E74, NF-188..NF-199): the options of the section
/// <c>IdentityVerification</c> and one partial hook per area of the plan. <b>Shared-file rule</b>: a task registers its services only in its own part,
/// <c>IdentityVerificationServiceCollectionExtensions.&lt;Hook&gt;.cs</c>, by implementing the hook declared here, for example
/// <c>static partial void AddPresenceServices(IServiceCollection services) { ... }</c>; it never edits this file nor <c>ServiceCollectionExtensions.cs</c>. A
/// hook nobody implements is dropped by the compiler, call included. Nothing here reads a flag: the services are registered always and read
/// <c>Features:CheckInIdentitySelfHosted</c> (and the other flags of the plan) when they run, so with the flags off the check-in behaves as before.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    public static IServiceCollection AddSelfHostedIdentityVerification(this IServiceCollection services)
    {
        services.AddOptions<IdentityVerificationOptions>()
            .BindConfiguration(IdentityVerificationOptions.SectionName);

        AddFoundationServices(services);
        AddSessionServices(services);
        AddDocumentReadingServices(services);
        AddDocumentGeometryServices(services);
        AddDocumentFieldServices(services);
        AddDocumentStepServices(services);
        AddPresenceServices(services);
        AddMatchingServices(services);
        AddDecisionServices(services);
        AddReviewServices(services);
        AddMetricsServices(services);
        return services;
    }

    static partial void AddFoundationServices(IServiceCollection services);

    static partial void AddSessionServices(IServiceCollection services);

    static partial void AddDocumentReadingServices(IServiceCollection services);

    static partial void AddDocumentGeometryServices(IServiceCollection services);

    static partial void AddDocumentFieldServices(IServiceCollection services);

    static partial void AddDocumentStepServices(IServiceCollection services);

    static partial void AddPresenceServices(IServiceCollection services);

    static partial void AddMatchingServices(IServiceCollection services);

    /// <summary>The decision of the versioned policy, the segments and the idempotent closure.</summary>
    static partial void AddDecisionServices(IServiceCollection services);

    static partial void AddReviewServices(IServiceCollection services);

    static partial void AddMetricsServices(IServiceCollection services);
}
