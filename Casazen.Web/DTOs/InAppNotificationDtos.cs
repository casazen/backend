using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// A notification of the bell (UI-12a). <b>No text, no name</b>: <see cref="Type"/> is a value of <c>PushTypes</c> (for instance
/// <c>new-booking</c> or <c>service-request-completed</c>) and the client writes the sentence from it, in the language of the user;
/// <see cref="EntityId"/> is the service request (the <c>service-request-*</c> types) or the booking it is about, the client builds
/// the link from the type and the id. A type the client does not know is shown with a generic sentence. The instants are UTC.
/// </summary>
/// <param name="Id">The notification, the id of <c>POST …/{id}/read</c>.</param>
/// <param name="Type">A value of <c>PushTypes</c>.</param>
/// <param name="EntityId">The service request or the booking the event is about; <c>null</c> when it names none.</param>
/// <param name="CreatedAt">When the event happened.</param>
/// <param name="ReadAt"><c>null</c> while unread.</param>
public sealed record InAppNotificationDto(Guid Id, string Type, Guid? EntityId, DateTime CreatedAt, DateTime? ReadAt)
{
    public static InAppNotificationDto From(InAppNotificationItem item) =>
        new(item.Id, item.Type, item.EntityId, item.CreatedAt, item.ReadAt);
}

/// <summary>The number of the bell: how many notifications of the caller are unread.</summary>
public sealed record UnreadNotificationCountDto(int Count);
