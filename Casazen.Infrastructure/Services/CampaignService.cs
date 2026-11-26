using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<CampaignService> logger,
    IApprovalQueueService? approvalQueue = null,
    IConfiguration? configuration = null) : ICampaignService
{
    public async Task<Campaign> CreateDraftAsync(
        Guid orgId,
        Guid segmentId,
        string subject,
        string body,
        bool isAiGenerated,
        CancellationToken cancellationToken = default, Guid? propertyId = null)
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
            PropertyId = propertyId,
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

        // Create approval queue item (AC-NF24-5). Since E05/F04 the queue creates it: it alerts the people who may decide and raises the "To do", and
        // approving the item runs the campaign's own action (CampaignApprovalAction). The new status of the campaign is saved by the same statement
        // that saves the item (one context), so the two never part.
        var queue = approvalQueue ?? throw new InvalidOperationException("The approval queue is not registered (AddApprovalQueueActions).");
        campaign.Status = CampaignStatus.PendingApproval;

        var queueItem = await queue.SubmitForApprovalAsync(
            orgId,
            ApprovalItemTypes.Campaign,
            campaignId,
            ApprovalQueueRules.CampaignTextOf(campaign.Subject, campaign.Body),
            campaign.IsAiGenerated,
            propertyId: campaign.PropertyId,
            originAction: ApprovalOriginActions.ApproveCampaign,
            cancellationToken: cancellationToken);

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

        if (campaign.Status == CampaignStatus.Scheduled && campaign.ScheduledAt == scheduledAt) return;
        if (campaign.Status != CampaignStatus.Approved)
            throw new DomainRuleException("campaign_not_approved", "Only approved campaigns can be scheduled");

        if (scheduledAt <= timeProvider.GetUtcNow().UtcDateTime)
            throw new DomainRuleException("invalid_schedule_time", "Scheduled time must be in the future");

        campaign.ScheduledAt = scheduledAt;
        campaign.Status = CampaignStatus.Scheduled;

        await db.SaveChangesAsync(cancellationToken);

        // Schedule Hangfire job for the specified time (AC-NF24-6)
        try
        {
            backgroundJobClient.Schedule<Jobs.CampaignSendJob>(job => job.SendCampaignAsync(campaignId, CancellationToken.None), scheduledAt);
        }
        catch
        {
            campaign.Status = CampaignStatus.Approved;
            campaign.ScheduledAt = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Campaign scheduled: campaignId={CampaignId}, scheduledAt={ScheduledAt}",
            campaignId, scheduledAt);
    }

    public async Task<Campaign> MarkApprovedAsync(
        Guid campaignId,
        Guid orgId,
        string? subject,
        string? body,
        CancellationToken cancellationToken = default)
    {
        if ((subject is null) != (body is null))
            throw new ArgumentException("The edited subject and the edited body come together: both, or neither.", nameof(subject));

        var campaign = await db.Campaigns
            .Where(c => c.Id == campaignId && c.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null)
            throw new DomainRuleException("campaign_not_found", $"Campaign {campaignId} not found");

        // A retry after a scheduling that failed (or after the item could not be saved) finds the work done. What the host approved last is the text that goes out, so an edit of this
        // call is applied (to a scheduled campaign too: the send job reads the campaign when it runs); with no edit the campaign is returned as it is.
        if (campaign.Status is CampaignStatus.Approved or CampaignStatus.Scheduled)
        {
            if (subject is not null && body is not null
                && (!string.Equals(campaign.Subject, subject, StringComparison.Ordinal) || !string.Equals(campaign.Body, body, StringComparison.Ordinal)))
            {
                CheckEditedText(subject, body);
                campaign.Subject = subject;
                campaign.Body = body;
                await db.SaveChangesAsync(cancellationToken);
            }

            return campaign;
        }

        if (campaign.Status != CampaignStatus.PendingApproval)
            throw new DomainConflictException("campaign_not_pending_approval", "Only campaigns waiting for approval can be approved");

        if (subject is not null && body is not null)
        {
            CheckEditedText(subject, body);

            campaign.Subject = subject;
            campaign.Body = body;
        }

        // The time asked for has passed while the campaign waited for the decision: it is approved and not scheduled, for the host to pick another time.
        if (campaign.ScheduledAt is { } scheduledAt && scheduledAt <= timeProvider.GetUtcNow().UtcDateTime)
            campaign.ScheduledAt = null;

        campaign.Status = CampaignStatus.Approved;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Campaign approved: campaignId={CampaignId}, orgId={OrgId}, edited={Edited}, hasTime={HasTime}",
            campaignId, orgId, subject is not null, campaign.ScheduledAt is not null);

        return campaign;
    }

    private static void CheckEditedText(string subject, string body)
    {
        if (subject.Length is 0 or > ApprovalQueueRules.CampaignSubjectMaxLength || string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("The edited subject is one line of at most 200 characters and the body is not empty.", nameof(subject));
    }

    public async Task ReturnToDraftAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.Campaigns
            .Where(c => c.Id == campaignId && c.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null || campaign.Status is not (CampaignStatus.PendingApproval or CampaignStatus.Approved or CampaignStatus.Scheduled))
        {
            logger.LogInformation(
                "Campaign {CampaignId} of org {OrgId} was not waiting for approval: nothing to return to a draft",
                campaignId, orgId);
            return;
        }

        // An approval that went halfway (the campaign approved or scheduled while its item was still waiting) is undone with its time: a rejected campaign is never left approved or
        // scheduled, and a send job that is still waiting finds a draft and sends nothing.
        if (campaign.Status is CampaignStatus.Approved or CampaignStatus.Scheduled)
            campaign.ScheduledAt = null;

        campaign.Status = CampaignStatus.Draft;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Campaign returned to a draft after a rejection: campaignId={CampaignId}", campaignId);
    }

    public Task<List<string>> GetRecipientsAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default)
        => EligibleEmailsAsync(segmentId, orgId, scope, null, cancellationToken);

    private async Task<List<string>> EligibleEmailsAsync(Guid segmentId, Guid orgId, HostScope scope, Guid? propertyId, CancellationToken cancellationToken)
    {
        var recipients = await ResolveAudienceAsync(segmentId, orgId, scope, propertyId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var month = now.Year * 100 + now.Month;
        var limit = await db.Set<MarketingStudioSettings>().Where(s => s.OrgId == orgId)
            .Select(s => (int?)s.MonthlyEmailLimit).SingleOrDefaultAsync(cancellationToken) ?? 2;
        if (limit <= 0) return [];
        var capped = (await db.CampaignDeliveries.AsNoTracking().Where(d => d.OrgId == orgId && d.Month == month)
            .GroupBy(d => d.ProfileId).Where(g => g.Count() >= Math.Min(limit, 2)).Select(g => g.Key)
            .ToListAsync(cancellationToken)).ToHashSet();
        return recipients.Where(r => !capped.Contains(r.GuestCrmProfileId!.Value)).Select(r => r.Value).ToList();
    }

    private IQueryable<GuestContact> CampaignEmailContacts() =>
        db.GuestContacts.AsNoTracking().Where(CrmContactRules.MarketableEmail)
            .Where(c => c.Confirmation == GuestContactConfirmation.Confirmed);

    private async Task<List<GuestContact>> ResolveAudienceAsync(Guid segmentId, Guid orgId, HostScope scope,
        Guid? propertyId, CancellationToken ct)
    {
        if (scope.OrgId != orgId)
            throw new DomainRuleException("invalid_scope", "Audience scope must belong to the campaign organization.");
        var segment = await db.GuestSegments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == segmentId && s.OrgId == orgId, ct)
            ?? throw new DomainRuleException("segment_not_found", "Segment not found");
        var condition = JsonSerializer.Deserialize<Condition>(segment.ConditionJson) ??
            throw new DomainRuleException("invalid_condition_json", "Invalid segment condition");
        if (conditionEvaluator.ValidateCondition(condition).Count > 0 ||
            condition.Groups.SelectMany(g => g.Rules).Any(rule => !MarketingSegmentRules.Fields.Any(f =>
                f.FieldId == rule.FieldId && f.Operators.Contains(rule.Operator)))) return [];
        HashSet<Guid>? properties = null;
        if (propertyId.HasValue || !scope.IsOrgWide)
        {
            properties = (await db.Properties.Where(p => p.OrgId == orgId).InScope(scope)
                .Where(p => !propertyId.HasValue || p.Id == propertyId).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        }

        var rows = await (from contact in CampaignEmailContacts()
                          join profile in db.GuestCrmProfiles.AsNoTracking() on contact.GuestCrmProfileId equals profile.Id
                          where contact.OrgId == orgId && profile.OrgId == orgId && profile.MergedIntoProfileId == null
                          orderby profile.Id, contact.ConfirmedAt descending, contact.Id
                          select new { contact, profile }).ToListAsync(ct);
        var result = new List<GuestContact>();
        var settings = await db.Set<MarketingStudioSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.OrgId == orgId, ct);
        var today = timeProvider.GetUtcNow().UtcDateTime;
        var suppressed = (await db.SuppressionListEntries.AsNoTracking().Where(e => e.OrgId == orgId)
            .Select(e => e.EmailHash).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (selected.Contains(row.profile.Id)) continue;
            if (properties != null && !MarketingSegmentRules.PropertyIds(row.profile).Any(properties.Contains)) continue;
            var stage = MarketingGuestCategories.Loyalty(row.profile.TotalStays, row.profile.LastStayAt, today, settings?.LoyalStays ?? 3, settings?.LostMonths ?? 24);
            if (!await conditionEvaluator.EvaluateAsync(condition, new MarketingSegmentFacts(row.profile, today, stage), scope, ct)) continue;
            if (suppressed.Contains(row.contact.ValueHash) || suppressed.Contains(SuppressionService.LegacyEmailHash(row.contact.Value))) continue;
            selected.Add(row.profile.Id);
            result.Add(row.contact);
        }
        return result;
    }

    private async Task<CampaignDelivery?> ReserveAsync(Campaign campaign, GuestContact contact, int limit, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        int month = now.Year * 100 + now.Month;
        var profileId = contact.GuestCrmProfileId!.Value;
        var history = db.CampaignDeliveries.AsNoTracking().Where(d => d.OrgId == campaign.OrgId && d.ProfileId == profileId);
        var previous = await db.CampaignDeliveries.FirstOrDefaultAsync(d => d.OrgId == campaign.OrgId && d.CampaignId == campaign.Id && d.ProfileId == profileId, ct);
        if (previous != null)
            return previous.Status == "Retry" && previous.ContactId == contact.Id && previous.CreatedAt > now.AddHours(-23) ? previous : null;
        var configuredLimit = configuration?.GetValue<int?>("Marketing:MonthlyOrganizationEmails");
        await using var transaction = configuredLimit.HasValue && db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (configuredLimit.HasValue)
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({campaign.OrgId.ToString()}, 981))", ct);
            if (await db.CampaignDeliveries.CountAsync(d => d.OrgId == campaign.OrgId && d.Month == month, ct) >= configuredLimit.Value)
                return null;
        }
        var used = await history.Where(d => d.Month == month).Select(d => d.Slot).ToListAsync(ct);
        for (int slot = 1; slot <= Math.Clamp(limit, 0, 2); slot++)
        {
            if (used.Contains(slot)) continue;
            var delivery = new CampaignDelivery { CampaignId = campaign.Id, OrgId = campaign.OrgId, ProfileId = profileId,
                ContactId = contact.Id, Month = month, Slot = slot, CreatedAt = now };
            db.CampaignDeliveries.Add(delivery);
            try
            {
                await db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return delivery;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(delivery).State = EntityState.Detached;
                if (await history.AnyAsync(d => d.CampaignId == campaign.Id, ct)) return null;
            }
        }
        return null;
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

        if (campaign.Status == CampaignStatus.Scheduled && campaign.ScheduledAt > timeProvider.GetUtcNow().UtcDateTime) return;

        // E07/F06/US01/T01: the endpoints are closed by Features:CrmMarketing, but this job is the way a campaign leaves and a send scheduled
        // before the module was closed is still in Hangfire. With the flag off nothing is sent, whatever the key and the secret say: the job
        // ends here (it does not fail, so Hangfire does not retry it) and a campaign that was scheduled goes back to Approved, an explicit state
        // the host can schedule again once the module is on, instead of staying Scheduled for a send that will not happen.
        if (!featureFlags.IsEnabled(FeatureFlags.CrmMarketing))
        {
            logger.LogWarning(
                "Campaign {CampaignId} of org {OrgId} not sent: Features:CrmMarketing is off (status was {Status}; a scheduled campaign goes back to Approved)",
                campaignId, campaign.OrgId, campaign.Status);
            if (campaign.Status == CampaignStatus.Scheduled)
            {
                campaign.Status = CampaignStatus.Approved;
                campaign.ScheduledAt = null;
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var org = await db.Orgs.AsNoTracking().SingleOrDefaultAsync(o => o.Id == campaign.OrgId, cancellationToken);
        if (org == null || !CommercialSignup.HasCrmMarketing(org)) return;

        // DEB-01: a marketing e-mail leaves only with its signed unsubscribe link. Without the secret or the public URL of the web app
        // nothing is sent and the job fails, so the error shows and the job is retried.
        if (!unsubscribeLinks.IsConfigured)
        {
            throw new EmailConfigurationException(
                $"Campaign {campaignId} not sent: {MarketingUnsubscribeLinks.SecretVariable} or App:PublicSiteBaseUrl is missing, " +
                "so the unsubscribe link cannot be built.");
        }

        // Re-evaluate segment recipients at send time (AC-NF24-2)
        var recipients = await ResolveAudienceAsync(campaign.SegmentId, campaign.OrgId, new HostScope(campaign.OrgId),
            campaign.PropertyId, cancellationToken);
        var limit = await db.Set<MarketingStudioSettings>().Where(s => s.OrgId == campaign.OrgId)
            .Select(s => (int?)s.MonthlyEmailLimit).SingleOrDefaultAsync(cancellationToken) ?? 2;
        foreach (var candidate in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contact = await CampaignEmailContacts()
                .SingleOrDefaultAsync(c => c.OrgId == campaign.OrgId && c.Id == candidate.Id &&
                    c.GuestCrmProfileId == candidate.GuestCrmProfileId, cancellationToken);
            if (contact == null || await suppressionService.IsSuppressedAsync(campaign.OrgId, contact.Value, cancellationToken)) continue;
            var delivery = await ReserveAsync(campaign, contact, limit, cancellationToken);
            if (delivery == null) continue;
            if (!await CampaignEmailContacts()
                    .AnyAsync(c => c.Id == contact.Id && c.OrgId == campaign.OrgId && c.GuestCrmProfileId == delivery.ProfileId, cancellationToken) ||
                await suppressionService.IsSuppressedAsync(campaign.OrgId, contact.Value, cancellationToken))
            {
                delivery.Status = "Suppressed";
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            var unsubscribe = unsubscribeLinks.Create(campaign.OrgId, contact.Value, delivery.Id);
            var enrichedBody = EnrichEmailBody(campaign.Body, campaignId, unsubscribe.PageUrl);
            try
            {
                var result = await emailService.SendCampaignEmailAsync(contact.Value, campaign.Subject, enrichedBody, UnsubscribeHeaders(unsubscribe), org.DisplayName, delivery.Id);
                delivery.Status = result.Success ? "Sent" : result.IsTransient ? "Retry" : "Failed";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Provider outcome unknown for campaign delivery {DeliveryId}", delivery.Id);
                delivery.Status = "Unknown";
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        var deliveries = db.CampaignDeliveries.Where(d => d.OrgId == campaign.OrgId && d.CampaignId == campaignId);
        campaign.SentCount = await deliveries.CountAsync(d => d.Status == "Sent", cancellationToken);
        campaign.ErrorCount = await deliveries.CountAsync(d => d.Status != "Sent", cancellationToken);
        var retryWindow = timeProvider.GetUtcNow().UtcDateTime.AddHours(-23);
        if (await deliveries.AnyAsync(d => d.Status == "Retry" && d.CreatedAt > retryWindow, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("The email provider requested a retry; reserved deliveries retain their idempotency keys.");
        }
        campaign.Status = CampaignStatus.Sent;
        campaign.SentAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Campaign {CampaignId} sent: {SentCount} successful, {ErrorCount} failed",
            campaignId, campaign.SentCount, campaign.ErrorCount);
    }

    public async Task<int> PreviewCountAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default, Guid? propertyId = null)
    {
        var recipients = await EligibleEmailsAsync(segmentId, orgId, scope, propertyId, cancellationToken);
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
