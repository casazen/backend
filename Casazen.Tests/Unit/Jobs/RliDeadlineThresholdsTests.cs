using Casazen.Web.BackgroundJobs;
using Xunit;
using Questura = Casazen.Web.BackgroundJobs.RliDeadlineReminderJob.QuesturaThresholds;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// LT-04 / LT-07: the thresholds of the RLI and Questura reminders are pure functions of the days left, so they run everywhere.
/// They sat in <c>RliDeadlineReminderJobTests</c>, whose lifecycle creates a PostgreSQL database before every test, theories
/// included: without a server the 17 cases failed with "No PostgreSQL server for integration tests" (QA-INFRA-01), and with
/// one each of them paid for a migrated database it never used. Moved here unchanged.
/// </summary>
public class RliDeadlineThresholdsTests
{
    [Theory]
    [InlineData(6, null)]
    [InlineData(5, Questura.BeforeDelivery)]
    [InlineData(3, Questura.BeforeDelivery)]
    [InlineData(2, Questura.Delivery)]
    [InlineData(1, Questura.Delivery)]
    [InlineData(0, Questura.Deadline)]
    [InlineData(-1, Questura.Overdue)]
    [InlineData(-90, Questura.Overdue)]
    public void QuesturaThresholds_Reached_MostUrgentThresholdOfTheDay(int daysToDeadline, string? expected)
    {
        Assert.Equal(expected, Questura.Reached(daysToDeadline));
    }

    [Theory]
    [InlineData(16, null)]
    [InlineData(15, RliDeadlineReminderJob.Thresholds.Days15)]
    [InlineData(8, RliDeadlineReminderJob.Thresholds.Days15)]
    [InlineData(7, RliDeadlineReminderJob.Thresholds.Days7)]
    [InlineData(2, RliDeadlineReminderJob.Thresholds.Days7)]
    [InlineData(1, RliDeadlineReminderJob.Thresholds.Days1)]
    [InlineData(0, RliDeadlineReminderJob.Thresholds.Days1)]
    [InlineData(-1, RliDeadlineReminderJob.Thresholds.Overdue)]
    [InlineData(-40, RliDeadlineReminderJob.Thresholds.Overdue)]
    public void Thresholds_Reached_MostUrgentThresholdOfTheDay(int daysRemaining, string? expected)
    {
        Assert.Equal(expected, RliDeadlineReminderJob.Thresholds.Reached(daysRemaining));
    }
}
