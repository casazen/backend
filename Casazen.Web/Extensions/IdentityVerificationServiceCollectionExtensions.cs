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
    /// <summary>
    /// Binds <see cref="IdentityVerificationOptions"/> to its section and runs the hooks in this order. Called once, by <c>AddCasazenServices</c>, next to the
    /// biometric registrations. The section is read from the application configuration of the container (<c>BindConfiguration</c>), so no configuration is passed.
    /// </summary>
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

    /// <summary>The foundations: the policy file, the registry of the formats, the closed-egress guards.</summary>
    static partial void AddFoundationServices(IServiceCollection services);

    /// <summary>The session bound to one guest: the opening, the notice, the choice, the closure.</summary>
    static partial void AddSessionServices(IServiceCollection services);

    /// <summary>The local reading of the document (the prefill of <c>read-document</c> included).</summary>
    static partial void AddDocumentReadingServices(IServiceCollection services);

    /// <summary>The geometry of the document: the card, its edges, its orientation, the zones of the layout.</summary>
    static partial void AddDocumentGeometryServices(IServiceCollection services);

    /// <summary>The fields of the document by zone and their coherence.</summary>
    static partial void AddDocumentFieldServices(IServiceCollection services);

    /// <summary>The document step of the self-hosted flow: the sides, the targeted guidance.</summary>
    static partial void AddDocumentStepServices(IServiceCollection services);

    /// <summary>The presence: the challenge and the validation of the sequence of frames.</summary>
    static partial void AddPresenceServices(IServiceCollection services);

    /// <summary>The match of the portrait with the frames chosen by the server.</summary>
    static partial void AddMatchingServices(IServiceCollection services);

    /// <summary>The decision of the versioned policy, the segments and the idempotent closure.</summary>
    static partial void AddDecisionServices(IServiceCollection services);

    /// <summary>The review by a person: the evidence, its time to live and its purge.</summary>
    static partial void AddReviewServices(IServiceCollection services);

    /// <summary>The counters and the export of the pilot.</summary>
    static partial void AddMetricsServices(IServiceCollection services);
}
