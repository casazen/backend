using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// E07/F07/US01: the recurring run of the owner portal operations does two kinds of work, the expiry alerts of the shared documents and the owner stays (the
/// automatic approvals, the blocks to give back, the sync results). Neither holds the other: a failure in one is logged and the other still runs, and the run ends in
/// error afterwards so that the next one tries again (nothing is swallowed).
/// </summary>
public class OwnerPortalOperationsJobTests
{
    private static readonly OwnerDocumentAlertResult NoAlerts = new(0, 0);
    private static readonly OwnerStayReconcileResult NoStays = new(0, 0, 0, 0, 0);

    private static (OwnerPortalOperationsJob Job, Mock<IOwnerStayService> Stays, Mock<IOwnerCollaborationService> Collaboration) Build(bool agencyTier = true)
    {
        var stays = new Mock<IOwnerStayService>();
        stays.Setup(s => s.ReconcileAsync(It.IsAny<CancellationToken>())).ReturnsAsync(NoStays);
        var collaboration = new Mock<IOwnerCollaborationService>();
        collaboration.Setup(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(NoAlerts);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AgencyTier)).Returns(agencyTier);
        return (new OwnerPortalOperationsJob(stays.Object, collaboration.Object, flags.Object, NullLogger<OwnerPortalOperationsJob>.Instance), stays, collaboration);
    }

    [Fact]
    public async Task ExecuteAsync_BothRun_WhenNothingFails()
    {
        var (job, stays, collaboration) = Build();

        await job.ExecuteAsync(CancellationToken.None);

        collaboration.Verify(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>()), Times.Once);
        stays.Verify(s => s.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TheExpiryAlertsFail_TheOwnerStaysAreStillLookedAfter_AndTheRunEndsInError()
    {
        var (job, stays, collaboration) = Build();
        collaboration.Setup(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a document failed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        Assert.Equal("a document failed", error.Message);
        stays.Verify(s => s.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TheOwnerStaysFail_TheExpiryAlertsHaveRun_AndTheRunEndsInError()
    {
        var (job, stays, collaboration) = Build();
        stays.Setup(s => s.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a stay failed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None));

        Assert.Equal("a stay failed", error.Message);
        collaboration.Verify(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BothFail_BothAreReported()
    {
        var (job, stays, collaboration) = Build();
        collaboration.Setup(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a document failed"));
        stays.Setup(s => s.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a stay failed"));

        var error = await Assert.ThrowsAsync<AggregateException>(() => job.ExecuteAsync(CancellationToken.None));

        Assert.Equal(["a document failed", "a stay failed"], error.InnerExceptions.Select(e => e.Message));
    }

    [Fact]
    public async Task ExecuteAsync_AgencyTierOff_DoesNothing()
    {
        var (job, stays, collaboration) = Build(agencyTier: false);

        await job.ExecuteAsync(CancellationToken.None);

        collaboration.Verify(c => c.RaiseExpiryAlertsAsync(It.IsAny<CancellationToken>()), Times.Never);
        stays.Verify(s => s.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
