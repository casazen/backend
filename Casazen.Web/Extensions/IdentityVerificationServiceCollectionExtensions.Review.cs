using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Jobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The review by a person of the self-hosted identity verification (E73/F01/US02, NF-195, ADR-013-R20, decision D9): the evidence kept for the reviewer, its time to live and its
/// purge. This is the part of the hook <c>AddReviewServices</c> (see <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and keep, serve and
/// purge nothing while <c>Features:CheckInIdentityReviewEvidence</c> and <c>Features:CheckInIdentitySelfHosted</c> are off (they read the flags when they run), so with the flags off the
/// check-in behaves as before. The hourly purge is scheduled in <c>RecurringJobsRegistration</c>, only while one of the two flags is on.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddReviewServices(IServiceCollection services)
    {
        // E73/F01/US02/T01, T02: the evidence of the reviewer (kept by the closure, read by the operators, deleted at the decision). Scoped: it holds the database of the request.
        services.AddScoped<IdentityReviewEvidenceStore>();

        // E73/F01/US02/T03: the retention of the evidence, of the abandoned sessions, of the checks of the document and of the verification logs, its job, and what the last run did
        // (one record for the process, read by the status of the platform administrators).
        services.AddScoped<IdentityRetentionService>();
        services.AddSingleton<IdentityRetentionRunLog>();
        services.AddScoped<IdentityEvidencePurgeJob>();
    }
}
