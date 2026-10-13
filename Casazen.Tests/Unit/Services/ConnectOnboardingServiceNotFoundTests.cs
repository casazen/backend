using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ConnectOnboardingServiceNotFoundTests
{
    [Fact]
    public async Task GetStatusAsync_UnknownOrg_ThrowsNotFoundExceptionWithOrgCode()
    {
        await using var db = CreateDb();
        var service = new ConnectOnboardingService(db, Mock.Of<IStripeConnectGateway>(), NullLogger<ConnectOnboardingService>.Instance);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetStatusAsync(Guid.NewGuid(), refreshFromStripe: false));

        Assert.Equal("org_not_found", ex.Code);
        Assert.Equal("OrganizationNotFound", ex.MessageKey);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_UnknownOrg_ThrowsNotFoundExceptionWithOrgCode()
    {
        await using var db = CreateDb();
        var gateway = new Mock<IStripeConnectGateway>();
        var service = new ConnectOnboardingService(db, gateway.Object, NullLogger<ConnectOnboardingService>.Instance);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.EnsureExpressAccountAsync(Guid.NewGuid()));

        Assert.Equal("org_not_found", ex.Code);
        gateway.VerifyNoOtherCalls();
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
