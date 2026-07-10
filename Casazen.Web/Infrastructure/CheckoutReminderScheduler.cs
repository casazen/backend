using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;

namespace Casazen.Web.Infrastructure;

public class CheckoutReminderScheduler(IBackgroundJobClient backgroundJobClient) : ICheckoutReminderScheduler
{
    public string ScheduleReminder(Guid bookingId, DateTime runAtUtc) =>
        backgroundJobClient.Schedule<CheckoutReminderJob>(
            job => job.SendReminderAsync(bookingId),
            runAtUtc);

    public void CancelReminder(string? jobId)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
            backgroundJobClient.Delete(jobId);
    }
}
