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
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(false, "SendGrid 403 Forbidden"));

        var service = CreateService(db, emailService.Object, isProduction: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", ["cleaning"], null));

        Assert.Contains("email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync("supplier@test.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateInviteAsync_WhenSendGridSucceeds_PersistsInvite()
    {
        await using var db = CreateDbContext();
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));

        var service = CreateService(db, emailService.Object, isProduction: true);

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, "Ciao");

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync(
                "supplier@test.com",
                "Invito CasaZen — Console fornitore",
                It.Is<string>(html => html.Contains("/login"))),
            Times.Once);
    }

    [Fact]
    public async Task CreateInviteAsync_InTesting_SkipsEmailWhenApiKeyMissing()
    {
        await using var db = CreateDbContext();
        var emailService = new Mock<IEmailService>();
        var service = CreateService(db, emailService.Object, isProduction: false, environmentName: "Testing");

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateInviteAsync_OnProductionWithoutSendGridKey_ThrowsBeforeCallingSendGrid()
    {
        await using var db = CreateDbContext();
        var emailService = new Mock<IEmailService>();
        var service = CreateService(
            db,
            emailService.Object,
            isProduction: false,
            environmentName: "Production",
            emailServiceApiKey: "SG.YOUR_KEY");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", null, null));

        Assert.Contains("Email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateInviteAsync_OnProductionWithNullEmailConfig_ThrowsBeforeCallingSendGrid()
    {
        await using var db = CreateDbContext();
        var emailService = new Mock<IEmailService>();
        var service = CreateService(
            db,
            emailService.Object,
            isProduction: false,
            environmentName: "Production",
            emailServiceApiKey: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", null, null));

        Assert.Contains("Email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateInviteAsync_OnProductionWithEmptyEmailConfig_ThrowsBeforeCallingSendGrid()
    {
        await using var db = CreateDbContext();
        var emailService = new Mock<IEmailService>();
        var service = CreateService(
            db,
            emailService.Object,
            isProduction: false,
            environmentName: "Production",
            emailServiceApiKey: string.Empty);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", null, null));

        Assert.Contains("Email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.SupplierInviteRecords);
        emailService.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    private static SupplierService CreateService(
        AppDbContext db,
        IEmailService emailService,
        bool isProduction,
        string environmentName = "Production",
        string? emailServiceApiKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicSiteBaseUrl"] = "https://casazen-app.vercel.app",
                ["Email:SendGridApiKey"] = emailServiceApiKey ?? (isProduction ? "SG.live_test_key" : string.Empty),
            })
            .Build();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        if (environmentName == "Development")
        {
            env.SetupGet(e => e.IsDevelopment()).Returns(true);
        }

        return new SupplierService(db, emailService, config, env.Object, NullLogger<SupplierService>.Instance);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
