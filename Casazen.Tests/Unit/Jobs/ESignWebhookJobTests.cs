using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// The job only hands the verified webhook body to <see cref="ILeaseSigningService"/>, where the flag check and the
/// lease status guard live (LT-02, A7-20; see <c>LeaseSigningServiceTests</c>).
/// </summary>
public class ESignWebhookJobTests
{
    [Fact]
    public async Task ProcessEventAsync_ValidPayload_DelegatesToTheSigningService()
    {
        var signing = new Mock<ILeaseSigningService>();
        var job = new ESignWebhookJob(signing.Object, Mock.Of<ILogger<ESignWebhookJob>>());

        await job.ProcessEventAsync("payload");

        signing.Verify(s => s.HandleProviderEventAsync("payload", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ServiceFails_RethrowsForHangfireRetry()
    {
        var signing = new Mock<ILeaseSigningService>();
        signing.Setup(s => s.HandleProviderEventAsync("payload", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ESignProviderException("download failed"));
        var job = new ESignWebhookJob(signing.Object, Mock.Of<ILogger<ESignWebhookJob>>());

        await Assert.ThrowsAsync<ESignProviderException>(() => job.ProcessEventAsync("payload"));
    }
}
