using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgAccessRequestService" />
/// <remarks>
/// <para><b>One unit of work, then the emails.</b> Under the lock of the person (<see cref="PostgresAdvisoryLocks.Scope.OrgAccessRequest"/>)
/// the requests of the last 24 hours are counted from the activity log; if the person has room, the line
/// <see cref="OrgActivityType.AccessRequested"/> is recorded and committed. Only then are the emails queued, one for each
/// administrator: a request that was refused or rolled back tells nobody, and an email that cannot be queued does not undo the
/// request (the line is the record that it was made; the answer says how many administrators were told).</para>
/// <para>The note is read once, into the emails. It is not stored, not logged and not in the activity log; the log keeps the
/// code of the area and the id of the person.</para>
/// </remarks>
public sealed class OrgAccessRequestService(
    AppDbContext db,
    IActivityLog activityLog,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IConfiguration configuration,
    ILogger<OrgAccessRequestService> logger,
    TimeProvider? timeProvider = null) : IOrgAccessRequestService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgAccessRequested> RequestAsync(RequestOrgAccess request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OrgAccessRequestRules.TryNormalizeArea(request.Area, out var area))
            throw new DomainRuleException(OrgAccessRequestErrors.AreaUnknown, "AccessRequestAreaUnknown");

        var requester = await OrgTeamAccess.RequireMemberAsync(db, request.OrgId, request.ActorUserId, cancellationToken);

        // A missing public URL is a configuration error, found before anything is saved (the email links to the people page).
        publicSiteLinks.EnsureConfigured();

        var limit = DailyLimit();
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgAccessRequest, $"{request.OrgId:N}:{request.ActorUserId}")))
        {
            var since = _clock.GetUtcNow().UtcDateTime - OrgAccessRequestRules.LimitWindow;
            var recent = await db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(
                    e => e.OrgId == request.OrgId
                         && e.Type == OrgActivityType.AccessRequested
                         && e.ActorUserId == request.ActorUserId
                         && e.When > since,
                    cancellationToken);
            if (recent >= limit)
                throw new DomainConflictException(OrgAccessRequestErrors.LimitReached, "AccessRequestLimitReached");

            activityLog.Record(OrgActivity.Of(
                request.OrgId,
                OrgActivityType.AccessRequested,
                request.ActorUserId,
                request.OrgId.ToString(),
                (OrgActivityDetailKeys.RequestedArea, area)));
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        var notified = await NotifyAsync(request, requester, area, cancellationToken);

        logger.LogInformation(
            "Org access requested: orgId={OrgId} userId={UserId} area={Area} notified={Notified}",
            request.OrgId, request.ActorUserId, area, notified);
        return new OrgAccessRequested(notified);
    }

    /// <summary>
    /// Queues the email to every active owner and administrator of the org (the holders of <c>org.members.manage</c>, see
    /// <see cref="OrgTeamRules.CanManageTeam"/>) whose account is active and has an address, but the requester itself.
    /// Returns how many emails the queue took.
    /// </summary>
    private async Task<int> NotifyAsync(RequestOrgAccess request, OrgMember requester, string area, CancellationToken cancellationToken)
    {
        var recipients = await (
                from m in db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                where m.OrgId == request.OrgId
                      && m.Status == OrgMemberStatus.Active
                      && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin)
                      && m.UserId != request.ActorUserId
                      && u.IsActive
                      && u.Email != string.Empty
                orderby m.CreatedAt, m.Id
                select new { u.Email, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            logger.LogWarning("Org access request of org {OrgId} has nobody to tell: no active owner or administrator with an email", request.OrgId);
            return 0;
        }

        var person = await db.Users.AsNoTracking()
            .Where(u => u.Id == request.ActorUserId)
            .Select(u => new { u.Email, u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        var requesterName = NameOf(person?.FirstName, person?.LastName, person?.Email);

        var culture = OrgInvitationEmails.CultureOf(OrgInvitationRules.NormalizeLanguage(request.Language));
        var note = OrgAccessRequestRules.NormalizeNote(request.Note);
        var peopleUrl = publicSiteLinks.AccountPeople();

        var queued = 0;
        foreach (var recipient in recipients)
        {
            var content = EmailTemplates.OrgAccessRequest(
                culture,
                NameOf(recipient.FirstName, recipient.LastName, recipient.Email),
                requesterName,
                requester.Role,
                area,
                note,
                peopleUrl);

            if (emailQueue.Enqueue(recipient.Email, content, EmailTemplates.Names.OrgAccessRequest))
            {
                queued++;
            }
            else
            {
                logger.LogWarning(
                    "The org access request of org {OrgId} was not queued for an administrator (provider not configured or queue down)",
                    request.OrgId);
            }
        }

        return queued;
    }

    private static string NameOf(string? firstName, string? lastName, string? email)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length > 0 ? name : email ?? string.Empty;
    }

    /// <summary>The requests a person may send in 24 hours: the configured whole number of at least 1, 3 otherwise.</summary>
    internal int DailyLimit() =>
        int.TryParse(configuration[OrgAccessRequestRules.DailyLimitConfigKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
        && limit >= 1
            ? limit
            : OrgAccessRequestRules.DefaultDailyLimit;
}
