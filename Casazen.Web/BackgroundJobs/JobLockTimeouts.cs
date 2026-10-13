namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Lock-wait timeouts for <see cref="Hangfire.DisableConcurrentExecutionAttribute"/> on background jobs (FD-11).
/// <para>
/// The timeout is how long a run waits for the previous run of the same job to release its lock. When it expires
/// the run fails with a lock timeout and Hangfire retries it later (<c>AutomaticRetry</c>), so two runs never touch
/// the same data at once. Locks live in the environment's own Hangfire schema, and the storage drops a lock older
/// than <c>Hangfire:DistributedLockTimeoutMinutes</c> (see <see cref="Configuration.HangfireStorageSettings"/>).
/// </para>
/// </summary>
public static class JobLockTimeouts
{
    /// <summary>Jobs that run every 5–15 minutes: wait briefly, the next occurrence comes soon anyway.</summary>
    public const int FrequentSeconds = 60;

    /// <summary>Hourly, daily and on-demand jobs.</summary>
    public const int DefaultSeconds = 300;
}
