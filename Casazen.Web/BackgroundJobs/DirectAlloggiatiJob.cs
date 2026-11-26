using Casazen.Infrastructure.External;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public sealed class DirectAlloggiatiJob(IServiceScopeFactory scopes, ILogger<DirectAlloggiatiJob> logger)
{
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task DispatchAsync(CancellationToken ct)
    {
        using var read = scopes.CreateScope();
        var ids = await read.ServiceProvider.GetRequiredService<DirectAlloggiatiDispatch>().CandidatesAsync(ct);
        foreach (var id in ids)
        {
            using var item = scopes.CreateScope();
            try { await item.ServiceProvider.GetRequiredService<DirectAlloggiatiDispatch>().ProcessAsync(id, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                // Provider exception text can contain fragments of a record. Only the local identifier and error type enter logs.
                logger.LogWarning("Alloggiati report {ReportId} could not be processed ({ErrorType})", id, error.GetType().Name);
            }
        }
    }

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ReceiptsAsync(CancellationToken ct)
    {
        using var read = scopes.CreateScope();
        var ids = await read.ServiceProvider.GetRequiredService<DirectAlloggiatiReceipts>().PendingAsync(ct);
        foreach (var id in ids)
        {
            using var item = scopes.CreateScope();
            try { await item.ServiceProvider.GetRequiredService<DirectAlloggiatiReceipts>().ArchiveAsync(id, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Alloggiati receipt for attempt {AttemptId} unavailable ({ErrorType})", id, error.GetType().Name);
            }
        }
    }
}
