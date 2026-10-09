using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// One-shot Hangfire job: ensure the official MEF profile of a comune after a property is created or its ISTAT
/// code changes. Failure of the enqueue does not block the HTTP request; the monthly job retries later.
/// </summary>
public class ComuneOfficialProfileJob(IComuneOfficialProfileService profiles)
{
    [DisableConcurrentExecution("ComuneOfficialProfileJob.EnsureAsync:{0}", JobLockTimeouts.DefaultSeconds)]
    public Task EnsureAsync(string istatCode, CancellationToken cancellationToken) =>
        profiles.EnsureAsync(istatCode, cancellationToken);
}
