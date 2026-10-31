using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-24: Campaign service proof tests for consent and suppression filtering.
/// These tests prove that privacy rules (consent required, suppression list checked) are enforced.
/// DEB-01: the unsubscribe link of a campaign is signed and carries no address.
/// </summary>
public class CampaignServiceTests
{
    private const string LinkSecret = "test-signed-link-secret-0123456789abcdef";
    private const string WebApp = "https://app.casazen.test";
    private const string Api = "https://api.casazen.test";

    /// <summary>The signed unsubscribe links as a configured environment builds them; no secret: not configured.</summary>
    private static MarketingUnsubscribeLinks Links(string? secret = LinkSecret) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MarketingUnsubscribeLinks.SecretVariable] = secret,
                ["App:ApiBaseUrl"] = Api,
            }).Build(),
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = WebApp })),
            TimeProvider.System);

    /// <summary>
    /// Proof test for DEB-01: the unsubscribe link of a campaign e-mail is the signed link of ADR-007-R18 on the web app (no address in
    /// it, the old one was unsigned and carried the address in the query), and the e-mail carries the one-click headers of RFC 8058.
    /// </summary>
    [Fact]
    public async Task Campaign_UnsubscribeLink_IsSignedWithoutTheAddress_AndTheHeadersOfferTheOneClick()
    {
        var (options, orgId, campaignId) = await SeedApprovedCampaignAsync("Guest.Person+promo@Example.com");
        var sent = new List<(string To, string Html, IReadOnlyDictionary<string, string> Headers)>();
        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendEmailWithHeadersAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>>((to, _, html, headers) => sent.Add((to, html, headers)))
            .ReturnsAsync(EmailSendResult.Sent());
        var links = Links();

        await using (var context = new AppDbContext(options))
            await NewService(context, email.Object, links).SendCampaignAsync(campaignId);

        var (to, html, headers) = Assert.Single(sent);
        Assert.Equal("Guest.Person+promo@Example.com", to);
        Assert.DoesNotContain("example.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%40", html);

        var emailHash = SuppressionService.ComputeEmailHash(to);
        var page = $"{WebApp}/unsubscribe#orgId={orgId:D}&amp;emailHash={emailHash}&amp;expiresAt=";
        Assert.Contains($"href=\"{page}", html);

        var oneClick = headers["List-Unsubscribe"];
        Assert.StartsWith($"<{Api}/api/public/marketing/unsubscribe?orgId={orgId:D}&emailHash={emailHash}&expiresAt=", oneClick);
        Assert.DoesNotContain("example.com", oneClick, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);

        // The values of the header are the ones the unsubscribe endpoint checks: valid as built, refused once tampered with.
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(oneClick.Trim('<', '>')).Query);
        var (expiresAt, signature) = (query["expiresAt"].ToString(), query["signature"].ToString());
        Assert.Equal(emailHash, query["emailHash"].ToString());
        Assert.Equal(MarketingUnsubscribeCheck.Valid, links.Verify(emailHash, expiresAt, signature));
        Assert.Equal(
            MarketingUnsubscribeCheck.InvalidSignature,
            links.Verify(SuppressionService.ComputeEmailHash("someone.else@example.com"), expiresAt, signature));

        await using var verify = new AppDbContext(options);
        var campaign = await verify.Campaigns.SingleAsync(c => c.Id == campaignId);
        Assert.Equal((CampaignStatus.Sent, 1, 0), (campaign.Status, campaign.SentCount, campaign.ErrorCount));
    }

    /// <summary>DEB-01: without the secret of the signed links a campaign sends nothing (no e-mail without its unsubscribe link).</summary>
    [Fact]
    public async Task Campaign_WithoutTheLinkSecret_SendsNothing()
    {
        var (options, _, campaignId) = await SeedApprovedCampaignAsync("guest@example.com");
        var email = new Mock<IEmailService>(MockBehavior.Strict);

        await using (var context = new AppDbContext(options))
        {
            await Assert.ThrowsAsync<EmailConfigurationException>(
                () => NewService(context, email.Object, Links(secret: null)).SendCampaignAsync(campaignId));
        }

        await using var verify = new AppDbContext(options);
        Assert.Equal(CampaignStatus.Approved, (await verify.Campaigns.SingleAsync(c => c.Id == campaignId)).Status);
    }

    private static CampaignService NewService(AppDbContext context, IEmailService email, MarketingUnsubscribeLinks links) =>
        new(
            context,
            new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>()),
            new SuppressionService(context, Mock.Of<ILogger<SuppressionService>>()),
            email,
            links,
            Mock.Of<IBackgroundJobClient>(),
            TimeProvider.System,
            Mock.Of<ILogger<CampaignService>>());

    /// <summary>One guest with marketing consent, a segment that takes everybody and an approved campaign to it.</summary>
    private static async Task<(DbContextOptions<AppDbContext> Options, Guid OrgId, Guid CampaignId)> SeedApprovedCampaignAsync(string guestEmail)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var orgId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        await using var context = new AppDbContext(options);
        context.Guests.Add(new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Email = guestEmail,
            FirstName = "Ospite",
            LastName = "Promo",
            MarketingConsent = true,
            MarketingConsentDate = DateTime.UtcNow.AddDays(-30),
            IsDeleted = false,
        });
        context.GuestSegments.Add(new GuestSegment
        {
            Id = segmentId,
            OrgId = orgId,
            Name = "All guests",
            ConditionJson = JsonSerializer.Serialize(new Condition { Version = "1.0", Groups = new List<ConditionGroup>() }),
        });
        context.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrgId = orgId,
            SegmentId = segmentId,
            Subject = "Offerta",
            Body = "<p>Torna a trovarci</p>",
            Status = CampaignStatus.Approved,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        return (options, orgId, campaignId);
    }

    /// <summary>
    /// Proof test for AC-NF24-2: proves that guests without marketing consent are excluded from campaign sends.
    /// Without this check, the org would send unwanted marketing emails to guests who never opted in (GDPR Article 6).
    /// </summary>
    [Fact]
    public async Task Campaign_ConsentRequired()
    {
        // Arrange: Set up in-memory database with guests (one with consent, one without)
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        var guestWithConsent = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Email = "consented@example.com",
            FirstName = "Alice",
            LastName = "Consented",
            MarketingConsent = true, // Explicit consent
            MarketingConsentDate = DateTime.UtcNow.AddDays(-30),
            IsDeleted = false
        };

        var guestWithoutConsent = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Email = "no-consent@example.com",
            FirstName = "Bob",
            LastName = "NoConsent",
            MarketingConsent = false, // NO consent
            IsDeleted = false
        };

        // Create segment with a condition that matches both guests
        var segment = new GuestSegment
        {
            Id = segmentId,
            OrgId = orgId,
            Name = "All guests",
            ConditionJson = JsonSerializer.Serialize(new Condition
            {
                Version = "1.0",
                Groups = new List<ConditionGroup>()
            })
        };

        await using (var context = new AppDbContext(options))
        {
            context.Guests.AddRange(guestWithConsent, guestWithoutConsent);
            context.GuestSegments.Add(segment);
            await context.SaveChangesAsync();
        }

        // Act: Get recipients for the segment
        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<CampaignService>>();
            var conditionEvaluator = new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>());
            var suppressionService = new SuppressionService(context, Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                timeProvider,
                logger);

            var scope = new HostScope(orgId);
            var recipients = await service.GetRecipientsAsync(segmentId, orgId, scope);

            // Assert: Only the guest with consent is included
            Assert.Single(recipients);
            Assert.Contains("consented@example.com", recipients);
            Assert.DoesNotContain("no-consent@example.com", recipients);
        }
    }

    /// <summary>
    /// Proof test for AC-NF24-11: proves that suppressed emails (one-click unsubscribe) are excluded from campaign sends.
    /// Without this check, unsubscribed users would receive unwanted emails after explicitly opting out (CAN-SPAM, GDPR).
    /// </summary>
    [Fact]
    public async Task Campaign_SuppressionListChecked()
    {
        // Arrange: Set up in-memory database with a guest who has consent but is suppressed
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        var suppressedEmail = "unsubscribed@example.com";

        var guestSuppressed = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Email = suppressedEmail,
            FirstName = "Charlie",
            LastName = "Unsubscribed",
            MarketingConsent = true, // Had consent initially
            MarketingConsentDate = DateTime.UtcNow.AddDays(-30),
            IsDeleted = false
        };

        var guestNotSuppressed = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Email = "active@example.com",
            FirstName = "Diana",
            LastName = "Active",
            MarketingConsent = true,
            MarketingConsentDate = DateTime.UtcNow.AddDays(-30),
            IsDeleted = false
        };

        // Create segment that matches both
        var segment = new GuestSegment
        {
            Id = segmentId,
            OrgId = orgId,
            Name = "All consented",
            ConditionJson = JsonSerializer.Serialize(new Condition
            {
                Version = "1.0",
                Groups = new List<ConditionGroup>()
            })
        };

        await using (var context = new AppDbContext(options))
        {
            context.Guests.AddRange(guestSuppressed, guestNotSuppressed);
            context.GuestSegments.Add(segment);
            await context.SaveChangesAsync();

            // Add suppressed email to suppression list (one-click unsubscribe)
            var suppressionService = new SuppressionService(context, Mock.Of<ILogger<SuppressionService>>());
            await suppressionService.AddToSuppressionListAsync(orgId, suppressedEmail, "unsubscribe");
        }

        // Act: Get recipients for the segment
        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<CampaignService>>();
            var conditionEvaluator = new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>());
            var suppressionService = new SuppressionService(context, Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                timeProvider,
                logger);

            var scope = new HostScope(orgId);
            var recipients = await service.GetRecipientsAsync(segmentId, orgId, scope);

            // Assert: Only the non-suppressed guest is included
            Assert.Single(recipients);
            Assert.Contains("active@example.com", recipients);
            Assert.DoesNotContain(suppressedEmail, recipients);
        }
    }

    /// <summary>
    /// Test that campaign status transitions follow the workflow: Draft -> PendingApproval -> Approved -> Scheduled -> Sent.
    /// </summary>
    [Fact]
    public async Task Campaign_StatusWorkflow()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        var segment = new GuestSegment
        {
            Id = segmentId,
            OrgId = orgId,
            Name = "Test segment",
            ConditionJson = JsonSerializer.Serialize(new Condition { Version = "1.0", Groups = new List<ConditionGroup>() })
        };

        await using (var context = new AppDbContext(options))
        {
            context.GuestSegments.Add(segment);
            await context.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<CampaignService>>();
            var conditionEvaluator = new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>());
            var suppressionService = new SuppressionService(context, Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                timeProvider,
                logger);

            // Act: Create draft
            var campaign = await service.CreateDraftAsync(
                orgId,
                segmentId,
                "Test subject",
                "<p>Test body</p>",
                false);

            Assert.Equal(CampaignStatus.Draft, campaign.Status);

            // Act: Submit for approval
            await service.SubmitForApprovalAsync(campaign.Id, orgId);

            var campaignAfterSubmit = await context.Campaigns.FindAsync(campaign.Id);
            Assert.NotNull(campaignAfterSubmit);
            Assert.Equal(CampaignStatus.PendingApproval, campaignAfterSubmit.Status);

            // Simulate approval (manually update status for this test)
            campaignAfterSubmit.Status = CampaignStatus.Approved;
            await context.SaveChangesAsync();

            // Act: Schedule
            var scheduledTime = DateTime.UtcNow.AddDays(1);
            await service.ScheduleAsync(campaign.Id, scheduledTime, orgId);

            var campaignAfterSchedule = await context.Campaigns.FindAsync(campaign.Id);
            Assert.NotNull(campaignAfterSchedule);
            Assert.Equal(CampaignStatus.Scheduled, campaignAfterSchedule.Status);
            Assert.Equal(scheduledTime, campaignAfterSchedule.ScheduledAt);
        }
    }
}
