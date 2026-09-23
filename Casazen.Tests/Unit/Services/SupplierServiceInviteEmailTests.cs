using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>FD-13: the supplier invite email is rendered from the template engine and queued, never sent inline.</summary>
public class SupplierServiceInviteEmailTests
{
    [Fact]
    public async Task CreateInviteAsync_ValidInvite_QueuesEmailWithSignupLinkFromPublicSiteBaseUrl()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", ["cleaning"], "Ciao");

        var stored = Assert.Single(db.SupplierInviteRecords);
        Assert.Equal(invite.InviteId, stored.Id);
        var (to, content, template) = Assert.Single(queue.Queued);
        Assert.Equal("supplier@test.com", to);
        Assert.Equal(EmailTemplates.Names.SupplierInvite, template);
        Assert.Equal("Invito CasaZen — Console fornitore", content.Subject);
        Assert.Contains(
            $"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/register?inviteToken={invite.InviteId}&amp;email=supplier%40test.com&amp;comune=H501\"",
            content.HtmlBody);
        Assert.DoesNotContain("railway.app", content.HtmlBody);
    }

    [Fact]
    public async Task CreateInviteAsync_MessageWithMarkup_IsHtmlEncodedInEmail()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateInviteAsync(
            "supplier@test.com", "H501", null, "<a href=\"https://phish.example\">Conferma IBAN</a>");

        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.DoesNotContain("<a href=\"https://phish.example\"", html);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Conferma IBAN&lt;/a&gt;", html);
    }

    [Fact]
    public async Task CreateInviteAsync_PublicSiteBaseUrlMissing_ThrowsConfigurationErrorWithoutSavingInvite()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue, publicSiteBaseUrl: null);

        await Assert.ThrowsAsync<EmailConfigurationException>(() =>
            service.CreateInviteAsync("supplier@test.com", "H501", null, null));

        Assert.Empty(db.SupplierInviteRecords);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task CreateInviteAsync_EmailNotQueued_KeepsInviteAndDoesNotThrow()
    {
        await using var db = CreateDbContext();
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(false);
        var service = new SupplierService(
            db, queue.Object, EmailTestHelpers.Links(), NullLogger<SupplierService>.Instance);

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
    }

    private static SupplierService CreateService(
        AppDbContext db,
        IEmailQueue queue,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl) =>
        new(db, queue, EmailTestHelpers.Links(publicSiteBaseUrl), NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
