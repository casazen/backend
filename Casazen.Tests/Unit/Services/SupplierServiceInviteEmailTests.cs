using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

        // SU-01: the link is the web app page with a random token only (no id, email or comune in the URL).
        var token = InviteToken(content.HtmlBody);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/register?inviteToken={token}\"", content.HtmlBody);
        Assert.DoesNotContain("railway.app", content.HtmlBody);
        Assert.DoesNotContain(invite.InviteId.ToString(), content.HtmlBody);
    }

    [Fact]
    public async Task CreateInviteAsync_ValidInvite_StoresOnlyTheHashOfTheLinkToken()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        var token = InviteToken(Assert.Single(queue.Queued).Content.HtmlBody);
        var stored = Assert.Single(db.SupplierInviteRecords);
        Assert.Equal(SupplierInviteTokens.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.True(SupplierInviteTokens.TryNormalize(token, out _));
    }

    [Fact]
    public async Task CreateInviteAsync_PilotComune_ShowsNameAndTruthfulNextStepsAndRomeExpiry()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var options = new SupplierRegistrationOptions
        {
            PilotComuni = [new SupplierPilotComune { Code = "H501", Name = "Roma" }],
        };
        var service = CreateService(db, queue, options: options);

        await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.Contains("per il comune <strong>Roma (H501)</strong>", html);
        Assert.Contains("procedura di attivazione", html);
        Assert.DoesNotContain("automaticamente", html);
        var expiresAt = Assert.Single(db.SupplierInviteRecords).ExpiresAt;
        var rome = TimeZoneInfo.ConvertTimeFromUtc(expiresAt, RomeCalendar.TimeZone);
        Assert.Contains($"L'invito scade il <strong>{rome:dd/MM/yyyy HH:mm}</strong> (ora italiana).", html);
    }

    [Fact]
    public async Task CreateInviteAsync_ComuneNotConfigured_ShowsCodeWithoutGuessingAName()
    {
        await using var db = CreateDbContext();
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        // F205 is Milano; the old registry says Firenze (A4-12): no name is derived from it.
        await service.CreateInviteAsync("supplier@test.com", "F205", null, null);

        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.Contains("per il comune <strong>F205</strong>", html);
        Assert.DoesNotContain("Firenze", html);
    }

    [Fact]
    public async Task CreateInviteAsync_LegacyPendingInviteWithoutTokenHash_DoesNotBlockNewInvite()
    {
        await using var db = CreateDbContext();
        db.SupplierInviteRecords.Add(new SupplierInviteRecord
        {
            Email = "supplier@test.com",
            ComuneCode = "H501",
            ExpiresAt = DateTime.UtcNow.AddDays(3),
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailQueue());

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        Assert.NotNull((await db.SupplierInviteRecords.FindAsync(invite.InviteId))!.TokenHash);
    }

    private static string InviteToken(string html)
    {
        var match = Regex.Match(html, "inviteToken=([0-9a-f]+)");
        Assert.True(match.Success, "The email has no invite link.");
        return match.Groups[1].Value;
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
            db,
            queue.Object,
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            Options.Create(new SupplierRegistrationOptions()),
            NullLogger<SupplierService>.Instance);

        var invite = await service.CreateInviteAsync("supplier@test.com", "H501", null, null);

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.Single(db.SupplierInviteRecords);
    }

    private static SupplierService CreateService(
        AppDbContext db,
        IEmailQueue queue,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        SupplierRegistrationOptions? options = null) =>
        new(
            db,
            queue,
            EmailTestHelpers.Links(publicSiteBaseUrl),
            Mock.Of<ISafeExternalHttpClient>(),
            Options.Create(options ?? new SupplierRegistrationOptions()),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
