using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICampaignService"/>
public class CampaignService(
    AppDbContext db,
    IConditionEvaluator conditionEvaluator,
    ISuppressionService suppressionService,
    Infrastructure.External.IEmailService emailService,
    IBackgroundJobClient backgroundJobClient,
    TimeProvider timeProvider,
    ILogger<CampaignService> logger) : ICampaignService
{
    public async Task<Campaign> CreateDraftAsync(
        Guid orgId,
        Guid segmentId,
        string subject,
        string body,
        bool isAiGenerated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);

        // Verify segment exists and belongs to org
        var segment = await db.GuestSegments
            .Where(s => s.Id == segmentId && s.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (segment is null)
            throw new DomainRuleException("segment_not_found", $"Segment {segmentId} not found for org {orgId}");

        var campaign = new Campaign
        {
            OrgId = orgId,
            SegmentId = segmentId,
            Subject = subject,
            Body = body,
            IsAiGenerated = isAiGenerated,
            Status = CampaignStatus.Draft,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Campaign draft created: id={CampaignId}, segmentId={SegmentId}, orgId={OrgId}, aiGenerated={IsAi}",
            campaign.Id, segmentId, orgId, isAiGenerated);

        return campaign;
    }

    public async Task SubmitForApprovalAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.Campaigns
            .Where(c => c.Id == campaignId && c.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null)
            throw new DomainRuleException("campaign_not_found", $"Campaign {campaignId} not found");

        if (campaign.Status != CampaignStatus.Draft)
            throw new DomainRuleException("campaign_not_draft", "Only draft campaigns can be submitted for approval");

        // Create approval queue item (AC-NF24-5)
        var expiresAt = timeProvider.GetUtcNow().AddDays(7).UtcDateTime;
        var queueItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Campaign",
            ItemId = campaignId,
            ContentText = $"{campaign.Subject}\n\n{campaign.Body}",
            ContentVersion = null,
            IsAiGenerated = campaign.IsAiGenerated,
            Status = ApprovalStatus.Pending,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            ExpiresAt = expiresAt
        };

        db.ApprovalQueueItems.Add(queueItem);

        campaign.Status = CampaignStatus.PendingApproval;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Campaign submitted for approval: campaignId={CampaignId}, queueId={QueueId}",
            campaignId, queueItem.Id);
    }

    public async Task ScheduleAsync(Guid campaignId, DateTime scheduledAt, Guid orgId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.Campaigns
            .Where(c => c.Id == campaignId && c.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null)
            throw new DomainRuleException("campaign_not_found", $"Campaign {campaignId} not found");

        if (campaign.Status != CampaignStatus.Approved)
            throw new DomainRuleException("campaign_not_approved", "Only approved campaigns can be scheduled");

        if (scheduledAt <= timeProvider.GetUtcNow().UtcDateTime)
            throw new DomainRuleException("invalid_schedule_time", "Scheduled time must be in the future");

        campaign.ScheduledAt = scheduledAt;
        campaign.Status = CampaignStatus.Scheduled;

        await db.SaveChangesAsync(cancellationToken);

        // Schedule Hangfire job for the specified time (AC-NF24-6)
        backgroundJobClient.Schedule<Jobs.CampaignSendJob>(
            job => job.SendCampaignAsync(campaignId, CancellationToken.None),
            scheduledAt);

        logger.LogInformation(
            "Campaign scheduled: campaignId={CampaignId}, scheduledAt={ScheduledAt}",
            campaignId, scheduledAt);
    }

    public async Task<List<string>> GetRecipientsAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        var segment = await db.GuestSegments
            .Where(s => s.Id == segmentId && s.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (segment is null)
            throw new DomainRuleException("segment_not_found", $"Segment {segmentId} not found");

        // Deserialize condition from JSON (ADR-014-R1)
        var condition = JsonSerializer.Deserialize<Condition>(segment.ConditionJson);
        if (condition is null)
            throw new DomainRuleException("invalid_condition_json", "Segment condition JSON is invalid");

        // Get all guests in the org with marketing consent (AC-NF24-2)
        var eligibleGuests = await db.Guests
            .Where(g => g.OrgId == orgId && g.MarketingConsent && !g.IsDeleted)
            .ToListAsync(cancellationToken);

        // Filter by segment condition (server-side evaluation, ADR-014-R3)
        var recipients = new List<string>();
        foreach (var guest in eligibleGuests)
        {
            var matches = await conditionEvaluator.EvaluateAsync(condition, guest, scope, cancellationToken);
            if (matches)
            {
                // Check suppression list (AC-NF24-11)
                var suppressed = await suppressionService.IsSuppressedAsync(orgId, guest.Email, cancellationToken);
                if (!suppressed)
                {
                    recipients.Add(guest.Email);
                }
            }
        }

        logger.LogInformation(
            "Recipients for segment {SegmentId}: {Count} eligible (from {TotalGuests} guests with consent)",
            segmentId, recipients.Count, eligibleGuests.Count);

        return recipients;
    }

    public async Task SendCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        // Re-fetch campaign inside the job (may run hours later)
        var campaign = await db.Campaigns
            .Include(c => c.Segment)
            .Where(c => c.Id == campaignId)
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null)
        {
            logger.LogWarning("Campaign {CampaignId} not found, job aborted", campaignId);
            return;
        }

        if (campaign.Status != CampaignStatus.Scheduled && campaign.Status != CampaignStatus.Approved)
        {
            logger.LogWarning(
                "Campaign {CampaignId} status is {Status}, expected Scheduled or Approved",
                campaignId, campaign.Status);
            return;
        }

        // Re-evaluate segment recipients at send time (AC-NF24-2)
        var scope = new HostScope(campaign.OrgId); // Job runs without user context, org-wide scope
        var recipients = await GetRecipientsAsync(campaign.SegmentId, campaign.OrgId, scope, cancellationToken);

        if (recipients.Count == 0)
        {
            logger.LogInformation(
                "Campaign {CampaignId} has no eligible recipients, marked as sent",
                campaignId);
            campaign.Status = CampaignStatus.Sent;
            campaign.SentAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        int sentCount = 0;
        int errorCount = 0;

        // Send emails (AC-NF24-7, AC-NF24-8, AC-NF24-9)
        foreach (var email in recipients)
        {
            try
            {
                // Build email body with UTM params and unsubscribe link
                var enrichedBody = EnrichEmailBody(campaign.Body, campaign.OrgId, campaignId, email);

                // Send via IEmailService (no tracking pixel, AC-NF24-8)
                var result = await emailService.SendEmailAsync(email, campaign.Subject, enrichedBody);
                if (result.Success)
                    sentCount++;
                else
                    errorCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending campaign {CampaignId} to {Email}", campaignId, email);
                errorCount++;
            }
        }

        // Update campaign stats (AC-NF24-10)
        campaign.SentCount = sentCount;
        campaign.ErrorCount = errorCount;
        campaign.Status = CampaignStatus.Sent;
        campaign.SentAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Campaign {CampaignId} sent: {SentCount} successful, {ErrorCount} failed",
            campaignId, sentCount, errorCount);
    }

    public async Task<int> PreviewCountAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        var recipients = await GetRecipientsAsync(segmentId, orgId, scope, cancellationToken);
        return recipients.Count;
    }

    /// <summary>
    /// Enriches email body with one-click unsubscribe link (AC-NF24-7).
    /// NOTE: UTM parameter enrichment (AC-NF24-9) is deferred - requires HTML parsing to find and modify links.
    /// </summary>
    private string EnrichEmailBody(string body, Guid orgId, Guid campaignId, string email)
    {
        // TODO AC-NF24-9: Add UTM parameters to links in the email body.
        // This requires HTML parsing to find <a href="..."> tags that point to the agency's direct booking site
        // and append utm_source=campaign&utm_medium=email&utm_campaign={campaignId} to the URLs.
        // Deferred to a future task due to complexity of safe HTML manipulation.

        // Generate one-click unsubscribe link (AC-NF24-7)
        // TODO: This should use a proper signed unsubscribe token service (shared with NF-05)
        var unsubscribeLink = $"https://app.casazen.com/unsubscribe?org={orgId}&email={Uri.EscapeDataString(email)}&campaign={campaignId}";

        // Append unsubscribe footer
        var footer = $@"
<hr style=""margin-top: 40px; border: none; border-top: 1px solid #e0e0e0;"">
<p style=""font-size: 12px; color: #666; text-align: center;"">
    <a href=""{unsubscribeLink}"" style=""color: #666; text-decoration: underline;"">Annulla iscrizione</a> |
    <a href=""{unsubscribeLink}"" style=""color: #666; text-decoration: underline;"">Unsubscribe</a>
</p>";

        // No tracking pixel (AC-NF24-8)
        return body + footer;
    }
}
