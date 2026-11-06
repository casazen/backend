using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Raises a popup alert for one user (ADR-025, NF-08, NF-22): stores it, holds it back until the end of the user's quiet hours when it
/// is not a legal alert and they are on, and otherwise pushes it to the open sessions of the user at once through
/// <see cref="IPopupAlertPublisher"/>.
/// </summary>
/// <remarks>
/// <para><b>Who calls it.</b> A handler of the event bus or a job, for a user: there is no request tenant, so the org of the row is the
/// host org of the alerted user, read from the database. A user with no active host org (a supplier-only account, a deactivated member,
/// an unknown id) is told nothing: no row is written.</para>
/// <para><b>Quiet hours</b> are the ones of <c>UserNotificationPreferences</c> (created with the defaults, 22:00-08:00 on the clock of
/// Europe/Rome, the first time an alert is routed to the user). An alert raised during them is stored with <c>RemindAt</c> set to the end
/// of the quiet hours and sent nothing now; the job <c>popup-alert-due</c> pushes it then. The legal alerts
/// (<see cref="AlertType.AlloggiatiDeadline"/>, <see cref="AlertType.IstatDeadline"/>) are never held back.</para>
/// <para><b>Idempotent while open.</b> An alert for the same user, type and entity that is not dismissed yet is not written twice, so a
/// handler that runs again (the event bus delivers at least once) never gives the user a second popup.</para>
/// <para><b>No personal data.</b> What is stored and pushed is the user id, the type, the entity reference and the urgency: never a
/// name or a text (ADR-025-R5).</para>
/// </remarks>
public interface IAlertRouter
{
    /// <param name="type">What the alert is about.</param>
    /// <param name="entityId">The entity the alert is about (a booking, a property...); the client reads its detail from the API.</param>
    /// <param name="entityType">The kind of that entity (<c>Booking</c>, <c>Property</c>, ...), at most 64 characters.</param>
    /// <param name="userId">The user who is alerted: the Auth0 subject (<c>User.Id</c>).</param>
    /// <param name="urgency">How urgent the alert is; the popup queue shows the most urgent first.</param>
    /// <param name="deadline">UTC instant of the legal deadline the alert is about, if any: the user cannot snooze the alert past it.</param>
    /// <param name="ct">Cancellation.</param>
    Task RouteAlertAsync(
        AlertType type,
        Guid entityId,
        string entityType,
        string userId,
        AlertUrgency urgency,
        DateTime? deadline,
        CancellationToken ct);
}

/// <summary>
/// Delivery of a popup alert to the open sessions of a user, in real time; nothing in it is personal data: the id of the alert, its type,
/// the entity it is about and its urgency (ADR-025-R5). The client reads the detail from the API of the entity.
/// </summary>
/// <remarks>
/// Best effort: an implementation never throws (it logs and swallows), because the alert is already stored and the client reads it from
/// <c>GET /api/me/alerts/pending</c> on the next page load or poll. The implementation is on SignalR (<c>Casazen.Web</c>,
/// <c>SignalRPopupAlertPublisher</c>, client method <c>alertReceived</c> of the hub <c>/hubs/notifications</c>).
/// </remarks>
public interface IPopupAlertPublisher
{
    /// <param name="userId">The user whose sessions are told (the token's <c>sub</c>).</param>
    /// <param name="alertId">The stored alert.</param>
    /// <param name="alertType">What the alert is about.</param>
    /// <param name="entityType">The kind of entity the alert is about.</param>
    /// <param name="entityId">The entity the alert is about.</param>
    /// <param name="urgency">How urgent the alert is.</param>
    /// <param name="ct">Cancellation.</param>
    Task PublishAsync(
        string userId,
        Guid alertId,
        AlertType alertType,
        string entityType,
        Guid entityId,
        AlertUrgency urgency,
        CancellationToken ct);
}

/// <summary>Publisher that does nothing: the default of hosts and tests with no real-time channel.</summary>
public sealed class NoOpPopupAlertPublisher : IPopupAlertPublisher
{
    public Task PublishAsync(
        string userId,
        Guid alertId,
        AlertType alertType,
        string entityType,
        Guid entityId,
        AlertUrgency urgency,
        CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
