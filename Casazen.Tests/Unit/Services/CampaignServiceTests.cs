using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
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
            TimeProvider.System,
            CrmTestIndex.Create());

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

        // E07/F06/US01/T02: the hash in the link is the keyed one (HMAC of the normalized address), never the plain SHA-256.
        var emailHash = CrmTestIndex.Create().Hash(ShowcaseBookingRules.NormalizeEmail(to));
        Assert.NotEqual(SuppressionService.LegacyEmailHash(to), emailHash);
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
            links.Verify(CrmTestIndex.Create().Hash("someone.else@example.com"), expiresAt, signature));

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

    /// <summary>
    /// Proof test (permission rule, E07/F06/US01/T01): the module is closed behind <c>Features:CrmMarketing</c> and the Hangfire job is the way a
    /// campaign leaves, so a campaign scheduled before the module was closed must not send a real e-mail while the flag is off. Whatever the state
    /// of the key and of the link secret (here both missing, so that the old code failed and the job was retried), no e-mail is attempted, the job
    /// does not fail, one warning in the log names the campaign and the flag, and the campaign goes back to Approved with no schedule: an explicit
    /// state the host can schedule again once the module is on, not a campaign left Scheduled for a send that will not happen.
    /// </summary>
    [Fact]
    public async Task SendCampaign_WithTheModuleOff_SendsNothingAndPutsTheCampaignBackToApproved_Test()
    {
        var (options, orgId, campaignId) = await SeedApprovedCampaignAsync("guest.person@example.com");
        await using (var seed = new AppDbContext(options))
        {
            var scheduled = await seed.Campaigns.SingleAsync(c => c.Id == campaignId);
            scheduled.Status = CampaignStatus.Scheduled;
            scheduled.ScheduledAt = DateTime.UtcNow.AddMinutes(-1);
            await seed.SaveChangesAsync();
        }

        var email = new Mock<IEmailService>();
        var log = new CapturingLogger<CampaignService>();
        await using (var context = new AppDbContext(options))
        {
            var withoutTheKey = CrmTestIndex.Create(key: null);
            var service = new CampaignService(
                context,
                new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>()),
                new SuppressionService(context, withoutTheKey, Mock.Of<ILogger<SuppressionService>>()),
                email.Object,
                Links(secret: null),
                Mock.Of<IBackgroundJobClient>(),
                new TestFeatureFlags(),
                TimeProvider.System,
                log);

            await service.SendCampaignAsync(campaignId);
        }

        email.VerifyNoOtherCalls();
        await using var verify = new AppDbContext(options);
        var campaign = await verify.Campaigns.SingleAsync(c => c.Id == campaignId);
        Assert.Equal(CampaignStatus.Approved, campaign.Status);
        Assert.Null(campaign.ScheduledAt);
        Assert.Null(campaign.SentAt);
        Assert.Equal((0, 0), (campaign.SentCount, campaign.ErrorCount));

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Features:CrmMarketing is off", warning.Message);
        Assert.Equal(campaignId, Assert.IsType<Guid>(warning.Values["CampaignId"]));
        Assert.Equal(orgId, Assert.IsType<Guid>(warning.Values["OrgId"]));
        Assert.DoesNotContain("example.com", log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static CampaignService NewService(AppDbContext context, IEmailService email, MarketingUnsubscribeLinks links) =>
        new(
            context,
            new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>()),
            new SuppressionService(context, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>()),
            email,
            links,
            Mock.Of<IBackgroundJobClient>(),
            ModuleOn(),
            TimeProvider.System,
            Mock.Of<ILogger<CampaignService>>());

    /// <summary>The flag of the marketing module as an environment that turned it on has it (E07/F06/US01/T01).</summary>
    private static IFeatureFlags ModuleOn() => new TestFeatureFlags(FeatureFlags.CrmMarketing);

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
            var suppressionService = new SuppressionService(context, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                ModuleOn(),
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
            var suppressionService = new SuppressionService(context, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>());
            await suppressionService.AddToSuppressionListAsync(orgId, suppressedEmail, "unsubscribe");
        }

        // Act: Get recipients for the segment
        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<CampaignService>>();
            var conditionEvaluator = new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>());
            var suppressionService = new SuppressionService(context, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                ModuleOn(),
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
            var suppressionService = new SuppressionService(context, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>());
            var backgroundJobClient = Mock.Of<IBackgroundJobClient>();
            var timeProvider = TimeProvider.System;

            // The queue creates the item of the approval (E05/F04): here a double that answers with one, so the workflow of the campaign itself is what is tested.
            var queue = new Mock<IApprovalQueueService>();
            queue
                .Setup(q => q.SubmitForApprovalAsync(
                    orgId,
                    ApprovalItemTypes.Campaign,
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<Guid?>(),
                    ApprovalOriginActions.ApproveCampaign,
                    It.IsAny<decimal?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApprovalQueueItem { OrgId = orgId, ItemType = ApprovalItemTypes.Campaign });

            var service = new CampaignService(
                context,
                conditionEvaluator,
                suppressionService,
                Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
                Links(),
                backgroundJobClient,
                ModuleOn(),
                timeProvider,
                logger,
                queue.Object);

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
            queue.Verify(
                q => q.SubmitForApprovalAsync(
                    orgId,
                    ApprovalItemTypes.Campaign,
                    campaign.Id,
                    "Test subject\n\n<p>Test body</p>",
                    false,
                    It.IsAny<string?>(),
                    It.IsAny<Guid?>(),
                    ApprovalOriginActions.ApproveCampaign,
                    It.IsAny<decimal?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Approval: the one place that sets Approved is the approval of the queue (E05/F04); before it nothing in production code did.
            await service.MarkApprovedAsync(campaign.Id, orgId, null, null);

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
