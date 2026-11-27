using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Jobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The review by a person of the self-hosted identity verification (E73/F01/US02, NF-195, ADR-013-R20, decision D9): the evidence kept for the reviewer, its time to live and its
/// purge. This is the part of the hook <c>AddReviewServices</c> (see <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and keep, serve and
/// purge nothing while <c>Features:CheckInIdentityReviewEvidence</c> and <c>Features:CheckInIdentitySelfHosted</c> are off (they read the flags when they run), so with the flags off the
/// check-in behaves as before. The hourly purge is scheduled in <c>RecurringJobsRegistration</c>, always: with both flags off it deletes only the evidence past its time.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddReviewServices(IServiceCollection services)
    {
        services.AddScoped<IdentityReviewEvidenceStore>();

        services.AddScoped<IdentityRetentionService>();
        services.AddSingleton<IdentityRetentionRunLog>();
        services.AddScoped<IdentityEvidencePurgeJob>();
    }
}
