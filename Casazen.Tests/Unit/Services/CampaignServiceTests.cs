using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-24: Campaign service proof tests for consent and suppression filtering.
/// These tests prove that privacy rules (consent required, suppression list checked) are enforced.
/// </summary>
public class CampaignServiceTests
{
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
