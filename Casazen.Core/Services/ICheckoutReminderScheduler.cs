namespace Casazen.Core.Services;

public interface ICheckoutReminderScheduler
{
    string ScheduleReminder(Guid bookingId, DateTime runAtUtc);

    void CancelReminder(string? jobId);
}
