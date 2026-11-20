using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the notification of the guests of a Ticino property tells the hosts of the property that an arrival day was declared uploaded to the police portal, or that
/// the declaration was undone (E30/F02/US02/T01, NF-128, decision D10, casazen/backend#979). A push to the hosts of the property that the decorator
/// (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell and the live <c>notificationsChanged</c> message of the open sessions, so the bell, the pop-up and the open
/// cockpit and Ticino pages of the other hosts read the days to notify again without a reload (<c>docs/runbooks/in-app-notifications.md</c>,
/// <c>docs/runbooks/guest-notification-switzerland.md</c> § 18).
/// </summary>
/// <remarks>
/// The text names the property and the day only, never a guest, a value or the person who acted (the cockpit shows who). Best effort, after the declaration is saved: whatever goes
/// wrong here is logged with ids and never undoes the declaration. It is not one of the eight kinds of event of the notification preferences (NF-65): it is the refresh of what a
/// co-host did, so it must reach the open pages of every host of the property whatever channel they chose for the deadlines, and it keeps the hosts of the property like the Ticino alerts
/// of E30/F02/US01/T01 (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason, as it lists the other live refreshes).
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
}
