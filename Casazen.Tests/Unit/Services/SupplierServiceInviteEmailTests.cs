using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SupplierServiceInviteEmailTests
{
    [Fact]
    public async Task CreateInviteAsync_WhenSendGridFails_RollsBackInviteAndThrows()
    {
        await using var db = CreateDbContext();
        var sendGrid = new Mock<ISendGridService>();
        sendGrid
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(false, "SendGrid 403 Forbidden"));

        var service = CreateService(db, sendGrid.Object, isProduction: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", ["cleaning"], null));

        Assert.Contains("email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        sendGrid.Verify(
            s => s.SendEmailAsync("supplier@test.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateInviteAsync_WhenSendGridSucceeds_PersistsInvite()
    {
        await using var db = CreateDbContext();
        var sendGrid = new Mock<ISendGridService>();
        sendGrid
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));

        var service = CreateService(db, sendGrid.Object, isProduction: true);

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, "Ciao");

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
        sendGrid.Verify(
            s => s.SendEmailAsync(
                "supplier@test.com",
                "Invito CasaZen — Console fornitore",
                It.Is<string>(html => html.Contains("inviteToken="))),
            Times.Once);
    }

    [Fact]
    public async Task CreateInviteAsync_InTesting_SkipsEmailWhenApiKeyMissing()
    {
        await using var db = CreateDbContext();
        var sendGrid = new Mock<ISendGridService>();
        var service = CreateService(db, sendGrid.Object, isProduction: false, environmentName: "Testing");

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
        sendGrid.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateInviteAsync_OnProductionWithoutSendGridKey_ThrowsBeforeCallingSendGrid()
    {
        await using var db = CreateDbContext();
        var sendGrid = new Mock<ISendGridService>();
        var service = CreateService(
            db,
            sendGrid.Object,
            isProduction: false,
            environmentName: "Production",
            sendGridApiKey: "SG.YOUR_KEY");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", null, null));

        Assert.Contains("SendGridApiKey", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        sendGrid.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    private static SupplierService CreateService(
        AppDbContext db,
        ISendGridService sendGrid,
        bool isProduction,
        string environmentName = "Production",
        string? sendGridApiKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicSiteBaseUrl"] = "https://casazen-app.vercel.app",
                ["Email:SendGridApiKey"] = sendGridApiKey ?? (isProduction ? "SG.live_test_key" : string.Empty),
            })
            .Build();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        if (environmentName == "Development")
        {
            env.SetupGet(e => e.IsDevelopment()).Returns(true);
        }

        return new SupplierService(db, sendGrid, config, env.Object, NullLogger<SupplierService>.Instance);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
