using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICampaignService"/>
public class CampaignService(
    AppDbContext db,
    IConditionEvaluator conditionEvaluator,
    ISuppressionService suppressionService,
    Infrastructure.External.IEmailService emailService,
    MarketingUnsubscribeLinks unsubscribeLinks,
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

        // DEB-01: a marketing e-mail leaves only with its signed unsubscribe link. Without the secret or the public URL of the web app
        // nothing is sent and the job fails, so the error shows and the job is retried.
        if (!unsubscribeLinks.IsConfigured)
        {
            throw new EmailConfigurationException(
                $"Campaign {campaignId} not sent: {MarketingUnsubscribeLinks.SecretVariable} or App:PublicSiteBaseUrl is missing, " +
                "so the unsubscribe link cannot be built.");
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
                // Build email body with UTM params and the signed unsubscribe link (DEB-01: no address in it)
                var unsubscribe = unsubscribeLinks.Create(campaign.OrgId, email);
                var enrichedBody = EnrichEmailBody(campaign.Body, campaignId, unsubscribe.PageUrl);

                // Send via IEmailService (no tracking pixel, AC-NF24-8), with the one-click unsubscribe headers
                var result = await emailService.SendEmailWithHeadersAsync(
                    email, campaign.Subject, enrichedBody, UnsubscribeHeaders(unsubscribe));
                if (result.Success)
                    sentCount++;
                else
                    errorCount++;
            }
            catch (Exception ex)
            {
                // Never the address in the log (DEB-01).
                logger.LogError(ex, "Error sending campaign {CampaignId} to one recipient", campaignId);
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
    /// <c>List-Unsubscribe</c> and <c>List-Unsubscribe-Post</c> (RFC 2369, RFC 8058): the mail client offers the unsubscribe itself and
    /// posts to the one-click endpoint. None when the API has no public https URL: the link in the body is still there.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> UnsubscribeHeaders(MarketingUnsubscribeLink link) =>
        link.OneClickUrl is { } oneClick
            ? new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{oneClick}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            }
            : new Dictionary<string, string>();

    /// <summary>
    /// Enriches email body with UTM params and the signed one-click unsubscribe link (AC-NF24-7, AC-NF24-9, DEB-01): the link is the page
    /// of the web app (<see cref="MarketingUnsubscribeLinks"/>), with the SHA-256 index of the address and never the address.
    /// </summary>
    private static string EnrichEmailBody(string body, Guid campaignId, string unsubscribeUrl)
    {
        // Add UTM parameters to links (AC-NF24-9)
        var utmParams = $"utm_source=campaign&utm_medium=email&utm_campaign={campaignId}";
        body = AddUtmParametersToLinks(body, utmParams);

        // One-click unsubscribe link (AC-NF24-7), added after the UTM parameters so it carries none.
        var unsubscribeLink = WebUtility.HtmlEncode(unsubscribeUrl);

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

    /// <summary>
    /// Adds UTM parameters to anchor tag href attributes in HTML content (AC-NF24-9).
    /// Uses regex to find and modify links. Preserves existing query parameters.
    /// </summary>
    private static string AddUtmParametersToLinks(string html, string utmParams)
    {
        // Pattern matches <a href="URL"> where URL doesn't already contain utm_source
        // This prevents double-adding UTM params and handles both " and ' quotes
        var pattern = @"<a\s+([^>]*\s+)?href\s*=\s*[""']([^""']+?)[""']";

        return System.Text.RegularExpressions.Regex.Replace(html, pattern, match =>
        {
            var fullMatch = match.Value;
            var url = match.Groups[2].Value;

            // Skip if already has UTM params or is not an HTTP(S) link
            if (url.Contains("utm_source=") ||
                !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return fullMatch;
            }

            // Append UTM params (use & if URL already has query params, otherwise ?)
            var separator = url.Contains('?') ? "&" : "?";
            var enrichedUrl = url + separator + utmParams;

            // Replace the URL in the original match
            return fullMatch.Replace($"\"{url}\"", $"\"{enrichedUrl}\"")
                            .Replace($"'{url}'", $"'{enrichedUrl}'");
        });
    }
}
