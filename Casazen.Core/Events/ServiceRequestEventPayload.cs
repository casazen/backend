using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <c>ServiceRequestCreated</c> and <c>ServiceRequestCompleted</c> (E24/F01/US01/T02, ADR-022-R21): a service request was made, or its work was
/// completed. Ids, enums and the category only: never the notes of the host or of the supplier, the address, the amounts or any person. A consumer (a routine, the
/// invoicing of the owners) reads the rest from the request by <c>EntityId</c>, in its own org. The enums are written by name, so a stored event stays readable if a
/// member is added.
/// </summary>
/// <remarks>
/// <c>BookingId</c> is the name the routines read to find the stay an event is about (<c>CreateActivity</c>, <c>CreateCleaningRequest</c>), so a routine on the completion
/// of a request can act on its stay. <see cref="RentalContext"/> tells a host's request from one born from a supplier's showcase: the org of the event is the org of the
/// request, which is the supplier's own for the second.
/// </remarks>
/// <param name="ServiceRequestId">The request (also the <c>EntityId</c> of the event).</param>
/// <param name="BookingId">The stay the request is for, when it is for one.</param>
/// <param name="PropertyId">The property of the request; <c>null</c> for a showcase request.</param>
/// <param name="SupplierOrgId">The supplier org.</param>
/// <param name="Category">The category code of the work (<c>cleaning</c>...).</param>
/// <param name="RentalContext">Where the request was made: a short-rent or long-rent host, or a supplier's showcase.</param>
/// <param name="Status">The status the request has after the change the event records.</param>
public sealed record ServiceRequestEventPayload(
    Guid ServiceRequestId,
    Guid? BookingId,
    Guid? PropertyId,
    Guid SupplierOrgId,
    string Category,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ServiceRequestRentalContext RentalContext,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ServiceRequestStatus Status)
{
    /// <summary>The prefix of the dedup key of <c>ServiceRequestCreated</c>: one event for each request.</summary>
    public const string CreatedKeyPrefix = "service-request-created";

    /// <summary>
    /// The prefix of the dedup key of <c>ServiceRequestCompleted</c>: one event for each request, whichever way it was completed (the supplier's completion, or the decision of a dispute
    /// that completes a job nobody marked), so every place that completes a request records it under the same key.
    /// </summary>
    public const string CompletedKeyPrefix = "service-request-completed";

    /// <summary>The dedup key of the event of <paramref name="requestId"/> with <paramref name="prefix"/> (<see cref="CreatedKeyPrefix"/> or <see cref="CompletedKeyPrefix"/>): <c>{prefix}:{id}</c>.</summary>
    public static string KeyOf(string prefix, Guid requestId) => $"{prefix}:{requestId:N}";

    /// <summary>The payload of <paramref name="request"/> as it is now, with <paramref name="status"/> as the status the event records.</summary>
    public static ServiceRequestEventPayload Of(ServiceRequest request, ServiceRequestStatus status)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ServiceRequestEventPayload(
            request.Id,
            request.BookingId,
            request.PropertyId,
            request.SupplierOrgId,
            request.Category,
            request.RentalContext,
            status);
    }
}
