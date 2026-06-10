using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class CinDeadlineAlertJobTests
{
    [Fact]
    public async Task ExecuteAsync_SendsAlert_WhenWithinSevenDaysAndNonCompliant()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        context.Properties.Add(new Property
        {
            Id = Guid.NewGuid(),
            OwnerId = "owner-1",
            Name = "Missing CIN",
            Address = "a",
            City = "Roma",
            IsActive = true,
            CinCode = null,
        });
        await context.SaveChangesAsync();

        var notificationMock = new Mock<INotificationService>();
        var job = new CinDeadlineAlertJob(
            context,
            notificationMock.Object,
            Mock.Of<ILogger<CinDeadlineAlertJob>>());

        var days = CinComplianceRules.DaysUntilDeadline();
        if (days > 7)
            return;

        await job.ExecuteAsync();

        notificationMock.Verify(
            n => n.SendCinDeadlineAlertAsync("owner-1", It.IsAny<IReadOnlyList<Guid>>(), days),
            Times.Once);
    }
}
