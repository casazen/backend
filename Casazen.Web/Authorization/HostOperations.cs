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
    public HostOperationRequirement(string contextKey, string permissionKey)
        : this([contextKey], permissionKey)
    {
    }
}

public static class PropertyOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "property.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "property.write");
}

public static class SharedPropertyOperations
{
    private static readonly string[] RentalContexts = ["short-rent", "long-rent"];

    public static readonly HostOperationRequirement Read = new(RentalContexts, "property.read");
    public static readonly HostOperationRequirement Write = new(RentalContexts, "property.write");
}

public static class BookingOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "booking.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "booking.write");
}

public static class GuestOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "guest.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "guest.write");
    public static readonly HostOperationRequirement Manage = new("short-rent", Casazen.Core.Authorization.HostPermissions.GuestManage);
}

public static class ServiceRequestOperations
{
    public static readonly HostOperationRequirement Write = new("short-rent", Casazen.Core.Authorization.HostPermissions.ServiceRequestWrite);
}

/// <summary>Operations on the Alloggiati communication of a stay (the resource is the property of the booking), apart from the booking itself (AM-03).</summary>
public static class AlloggiatiOperations
{
    public static readonly HostOperationRequirement Submit = new("short-rent", Casazen.Core.Authorization.HostPermissions.AlloggiatiSubmit);
}

public static class PaymentOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "payment.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "payment.write");
}

public static class OtaOperations
{
    public static readonly HostOperationRequirement Read = new("short-rent", "ota.read");
    public static readonly HostOperationRequirement Write = new("short-rent", "ota.write");
}

public static class LongRentPropertyOperations
{
    public static readonly HostOperationRequirement Read = new("long-rent", "property.read");
    public static readonly HostOperationRequirement Write = new("long-rent", "property.write");
}

public static class LeaseOperations
{
    public static readonly HostOperationRequirement Read = new("long-rent", "lease.read");
    public static readonly HostOperationRequirement Create = new("long-rent", "lease.create");
    public static readonly HostOperationRequirement Sign = new("long-rent", "lease.sign");
    public static readonly HostOperationRequirement Register = new("long-rent", "lease.register");
}
