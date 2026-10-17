using Casazen.Core.Entities;
using Casazen.Web.BackgroundJobs;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// UI-12a: the runbook of the in-app notifications must say what the code does (the job, the four endpoints, the indexes, the
/// retention), and the pages that list jobs, variables and runbooks carry the new ones.
/// </summary>
public class InAppNotificationsRunbookTests
{
    private static string Docs => Path.Combine(FindRepositoryRoot(), "docs");

    private static string Runbook(string name) => File.ReadAllText(Path.Combine(Docs, "runbooks", name));

    [Fact]
    public void Runbook_NamesTheJobsTheEndpointsTheIndexesAndTheRetention()
    {
        var runbook = Runbook("in-app-notifications.md");

        Assert.Contains($"`{InAppNotificationRetentionJob.RecurringJobId}`", runbook);
        Assert.Contains("`/api/me/notifications?", runbook);
        Assert.Contains("/api/me/notifications/unread-count", runbook);
        Assert.Contains("/api/me/notifications/{id}/read", runbook);
        Assert.Contains("/api/me/notifications/read-all", runbook);
        Assert.Contains(InAppNotification.OncePerEventAndUserIndexName, runbook);
        Assert.Contains("IX_InAppNotifications_UserId_ReadAt_CreatedAt", runbook);
        Assert.Contains($"{InAppNotificationLimits.RetentionDays} days", runbook);
        Assert.Contains("DELETE FROM", runbook);
        Assert.Contains("AddInAppNotifications", runbook);
    }

    [Fact]
    public void TheListsOfJobsVariablesAndRunbooks_CarryTheNewOnes()
    {
        Assert.Contains($"`{InAppNotificationRetentionJob.RecurringJobId}`", Runbook("hangfire.md"));
        Assert.Contains("`in-app-notifications.md`", Runbook("index.md"));
        Assert.Contains("InAppNotifications", Runbook("gdpr.md"));
        var technical = File.ReadAllText(Path.Combine(Docs, "TECHNICAL.md"));
        Assert.Contains("/api/me/notifications", technical);
        Assert.Contains("#### `InAppNotification` (UI-12a)", technical);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
