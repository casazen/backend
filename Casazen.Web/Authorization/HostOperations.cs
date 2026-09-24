using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

/// <summary>
/// A resource-based operation on a <see cref="Casazen.Core.Authorization.HostResource"/>: the context permission it
/// needs, held in at least one of <see cref="ContextKeys"/>. Handled only by <see cref="HostResourceAuthorizationHandler"/>,
/// which adds the org and ownership checks; it is a distinct type from the policy requirement on purpose, so a
/// permission alone never authorizes a row.
/// </summary>
public sealed record HostOperationRequirement(IReadOnlyList<string> ContextKeys, string PermissionKey) : IAuthorizationRequirement
{
    /// <summary>An operation that needs <paramref name="permissionKey"/> in one context.</summary>
    public HostOperationRequirement(string contextKey, string permissionKey)
        : this([contextKey], permissionKey)
    {
    }
}

/// <summary>Operations on a property's short-stay side and on the rows that follow it (pricing, iCal, OTA, service requests).</summary>
public static class PropertyOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "property.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "property.write");
}

/// <summary>
/// Operations on the property core shared by both rental contexts (record, documents): <c>property.*</c> in short-rent
/// or long-rent (<see cref="CasazenPolicies.SharedPropertyRead"/>, A7-06).
/// </summary>
public static class SharedPropertyOperations
{
    private static readonly string[] RentalContexts = ["short-rent", "long-rent"];

    public static readonly HostOperationRequirement Read = new(RentalContexts, "property.read");
    public static readonly HostOperationRequirement Write = new(RentalContexts, "property.write");
}

/// <summary>Operations on a booking (the resource is its property).</summary>
public static class BookingOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "booking.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "booking.write");
}

/// <summary>Operations on a guest of the org (org-level resource, no owner).</summary>
public static class GuestOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "guest.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "guest.write");
}

/// <summary>Operations on a payment (the resource is the property of its booking).</summary>
public static class PaymentOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "payment.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "payment.write");
}

/// <summary>Operations on the OTA integrations of a property.</summary>
public static class OtaOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "ota.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "ota.write");
}

/// <summary>Operations on a long-term lease (the resource is its property, with the lease's own org).</summary>
public static class LeaseOperations
{
    public static readonly HostOperationRequirement Read = new("long-rent", "lease.read");
    public static readonly HostOperationRequirement Create = new("long-rent", "lease.create");
    public static readonly HostOperationRequirement Sign = new("long-rent", "lease.sign");
    public static readonly HostOperationRequirement Register = new("long-rent", "lease.register");
}
