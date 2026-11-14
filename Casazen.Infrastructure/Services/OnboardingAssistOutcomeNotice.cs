using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where «Fatto» on a «Do it for me» request tells the host (E20/F03/US01/T02, the rule of the live notifications): every administrator of
/// the org of the request (the owner and the <c>Admin</c>s, who alone can ask for help) gets a push on the phone, an entry of the bell and its live
/// update, so the open page reads the request again and shows the pop-up. The type says what «Fatto» recorded: <see cref="PushTypes.AssistVerified"/>
/// («Il collegamento è attivo») or <see cref="PushTypes.AssistDeclared"/> («Il supporto dichiara di aver collegato …»).
/// </summary>
/// <remarks>
/// <para>Each person is told once per «Fatto» (<see cref="PushDeliveryKeys.AssistOutcome"/>, also the key of the bell row): a request reopened and done
/// again is another event. The push of one person carries no bell entry of its own (<see cref="PushAudienceKind.User"/>), so the entry is written here,
/// with the request as its entity, and published live.</para>
/// <para>No text of the request, no property, no supplier and no person in the push or the entry. Best effort, after the update is committed: whatever
/// goes wrong is logged with the ids and never fails the staff update. <b>Each administrator is told on its own</b>: a failure for one (its bell row, its
/// push) is logged and the others are still told, so one person who cannot be reached never leaves the rest of the org unaware. It is not one of the eight
/// kinds of event of the notification preferences (NF-65), so it keeps the administrators of the org (<c>HostNotifyingPathsArchitectureTests</c> lists it
/// for that reason).</para>
/// </remarks>
internal static class OnboardingAssistOutcomeNotice
{
    public static async Task TellAsync(
        AppDbContext db,
        IPushNotificationService pushNotifications,
        IInAppNotificationPublisher? publisher,
        OnboardingAssistRequest request,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Outcome is not { } outcome || request.CompletedAt is not { } doneAt)
            return;

        var verified = outcome == OnboardingAssistOutcomes.Verified;
        var type = verified ? PushTypes.AssistVerified : PushTypes.AssistDeclared;
        PushText text;
        List<string> administrators;
        try
        {
            text = verified
                ? EmailTemplates.AssistVerifiedPush(EmailTemplates.DefaultCulture)
                : EmailTemplates.AssistDeclaredPush(EmailTemplates.DefaultCulture);
            administrators = await HostNotificationAudience.AdministratorsToTell(db, request.OrgId).Select(u => u.Id).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The administrators of org {OrgId} could not be found to tell them that assist request {RequestId} is done", request.OrgId, request.Id);
            return;
        }

        // Each administrator is told on its own: a failure for one (its bell row, its push) is logged and the others are still told.
        var told = 0;
        var failed = 0;
        foreach (var userId in administrators.Order(StringComparer.Ordinal))
        {
            try
            {
                if (await TellOneAsync(db, pushNotifications, publisher, request, type, text, userId, doneAt, ct))
                    told++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "An administrator of org {OrgId} could not be told that assist request {RequestId} is done", request.OrgId, request.Id);
            }
        }

        logger.LogInformation(
            "Assist request {RequestId} of org {OrgId} done ({Outcome}): {Count} administrator(s) told, {Failed} could not be",
            request.Id, request.OrgId, outcome, told, failed);
    }

    /// <summary>One administrator: the push to its phone, the entry of its bell and the live update. False when its bell entry was there already.</summary>
    private static async Task<bool> TellOneAsync(
        AppDbContext db,
        IPushNotificationService pushNotifications,
        IInAppNotificationPublisher? publisher,
        OnboardingAssistRequest request,
        string type,
        PushText text,
        string userId,
        DateTime doneAt,
        CancellationToken ct)
    {
        var key = PushDeliveryKeys.AssistOutcome(request.Id, doneAt, userId);
        pushNotifications.Enqueue(
            key,
            PushAudience.User(request.OrgId, userId),
            new PushNotificationPayload(text.Title, text.Body, type, BookingId: null, PushRoutes.Properties));
        if (!await WriteBellAsync(db, request, type, key, userId, doneAt, ct))
            return false;

        if (publisher is not null)
            await publisher.PublishAsync(userId, type, request.Id, ct);

        return true;
    }

    /// <summary>Writes the entry of the bell of the person, once for each «Fatto». False when it was there already.</summary>
    private static async Task<bool> WriteBellAsync(
        AppDbContext db,
        OnboardingAssistRequest request,
        string type,
        string key,
        string userId,
        DateTime doneAt,
        CancellationToken ct)
    {
        if (await db.InAppNotifications
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .AnyAsync(n => n.DeliveryKey == key && n.UserId == userId, ct))
        {
            return false;
        }

        var row = new InAppNotification
        {
            OrgId = request.OrgId,
            UserId = userId,
            Type = type,
            EntityId = request.Id,
            DeliveryKey = key,
            CreatedAt = UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(doneAt)),
        };
        db.InAppNotifications.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, InAppNotification.OncePerEventAndUserIndexName))
        {
            // The same «Fatto» written for the person at the same moment: that row is the one.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
        catch (Exception)
        {
            // A row that could not be saved must not stay in the tracker, or every later save of this context would try it again.
            db.Entry(row).State = EntityState.Detached;
            throw;
        }

        return true;
    }
}
