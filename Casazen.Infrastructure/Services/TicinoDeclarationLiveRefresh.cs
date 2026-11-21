using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the notification of the guests to the authority tells the hosts of a property that an arrival day was declared notified, or that the declaration was undone: for a
/// Ticino property (<see cref="Tell"/>: uploaded to the police portal, E30/F02/US02/T01, NF-128, decision D10, casazen/backend#979) and for a property of another canton the assisted
/// notification covers (<see cref="TellSwiss"/>: notified to the cantonal authority, E31/F02/US01/T01, NF-131). A push to the hosts of the property that the decorator
/// (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell and the live <c>notificationsChanged</c> message of the open sessions, so the bell, the pop-up and the open
/// cockpit and Ticino pages of the other hosts read the days to notify again without a reload (<c>docs/runbooks/in-app-notifications.md</c>,
/// <c>docs/runbooks/guest-notification-switzerland.md</c> § 18 and § 26).
/// </summary>
/// <remarks>
/// The text names the property and the day only, never a guest, a value or the person who acted (the cockpit shows who). Best effort, after the declaration is saved: whatever goes
/// wrong here is logged with ids and never undoes the declaration. It is not one of the eight kinds of event of the notification preferences (NF-65): it is the refresh of what a
/// co-host did, so it must reach the open pages of every host of the property whatever channel they chose for the deadlines, and it keeps the hosts of the property like the Ticino alerts
/// of E30/F02/US01/T01 (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason, as it lists the other live refreshes). The second method lives in the same file on purpose: the
/// file is the one entry of that list, and the two pushes are the same event for two regimes.
/// </remarks>
internal static class TicinoDeclarationLiveRefresh
{
    /// <summary>Queues the push of a declaration (<paramref name="declared"/> true) or of its undo; nothing without a queue.</summary>
    public static void Tell(
        IPushNotificationService? push,
        Guid orgId,
        Guid propertyId,
        string propertyName,
        DateOnly day,
        bool declared,
        DateTime atUtc,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (push is null)
            return;

        try
        {
            var text = EmailTemplates.TicinoNotificationDeclaredPush(EmailTemplates.DefaultCulture, propertyName, day, declared);
            if (!push.Enqueue(
                    PushDeliveryKeys.TicinoNotificationDeclared(propertyId, day, declared, atUtc),
                    PushAudience.PropertyHosts(propertyId),
                    new PushNotificationPayload(text.Title, text.Body, PushTypes.TicinoNotificationDeclared, null, PushRoutes.Properties)))
            {
                logger.LogWarning("Ticino declaration push of property {PropertyId} not queued (org {OrgId})", propertyId, orgId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Ticino declaration push of property {PropertyId} could not be prepared (org {OrgId}) ({Error})", propertyId, orgId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The same for the notification of a canton other than Ticino (E31/F02/US01/T01): the push of a declaration (<paramref name="declared"/> true) or of its undo, of type
    /// <see cref="PushTypes.SwissNotificationDeclared"/>; nothing without a queue.
    /// </summary>
    public static void TellSwiss(
        IPushNotificationService? push,
        Guid orgId,
        Guid propertyId,
        string propertyName,
        DateOnly day,
        bool declared,
        DateTime atUtc,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (push is null)
            return;

        try
        {
            var text = EmailTemplates.SwissNotificationDeclaredPush(EmailTemplates.DefaultCulture, propertyName, day, declared);
            if (!push.Enqueue(
                    PushDeliveryKeys.SwissNotificationDeclared(propertyId, day, declared, atUtc),
                    PushAudience.PropertyHosts(propertyId),
                    new PushNotificationPayload(text.Title, text.Body, PushTypes.SwissNotificationDeclared, null, PushRoutes.Properties)))
            {
                logger.LogWarning("Swiss notification declaration push of property {PropertyId} not queued (org {OrgId})", propertyId, orgId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Swiss notification declaration push of property {PropertyId} could not be prepared (org {OrgId}) ({Error})", propertyId, orgId, ex.GetType().Name);
        }
    }
}
