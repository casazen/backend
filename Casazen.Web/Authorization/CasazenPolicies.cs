namespace Casazen.Web.Authorization;

/// <summary>
/// Every authorization policy of the API (TN-3). Controllers reference these constants, never string literals, and
/// <c>AddCasazenAuthorization</c> registers exactly this set: a policy that is not here does not exist, and the
/// architecture tests fail for a policy registered but never used or used but never registered.
/// </summary>
/// <remarks>
/// <para><b>How to choose.</b> Host endpoints use a context policy (<c>RequireContext:{context}:{permission}</c>),
/// resolved from DB memberships with the JWT roles as fallback (<c>ContextAuthorizationService</c>): the class carries
/// the read permission, each writing action adds the write permission. The policy only says "this user may do this
/// kind of thing"; the row itself is checked with <c>IAuthorizationService.AuthorizeAsync(User, HostResource, operation)</c>
/// (<see cref="PropertyOperations"/>), which verifies org, permission and property ownership.</para>
/// <para><b>Properties.</b> Both rental contexts hold <c>property.*</c>, but a permission counts only in its own
/// context: the property core a long-term landlord needs (list, record, documents) uses <see cref="SharedPropertyRead"/> /
/// <see cref="SharedPropertyWrite"/>, everything about short stays uses <see cref="PropertyRead"/> /
/// <see cref="PropertyWrite"/>, so a landlord with only the long-rent context never reaches bookings, calendars or OTA.</para>
/// <para><see cref="Authenticated"/> is for user-scoped endpoints only (own profile, own devices, onboarding): every
/// action whose only policy is <see cref="Authenticated"/> (or a bare <c>[Authorize]</c>) must be listed, with a reason,
/// in the allow-list of <c>EndpointAuthorizationArchitectureTests</c>.</para>
/// </remarks>
public static class CasazenPolicies
{
    /// <summary>Any signed-in user, suppliers included. User-scoped endpoints only (see remarks).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Platform administrator (JWT role <c>Admin</c>).</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Supplier console (JWT role <c>Supplier</c>, backfilled from the DB supplier link).</summary>
    public const string Supplier = "RequireSupplier";

    /// <summary>Administrator of the caller's org: plan, billing, domain.</summary>
    public const string OrgBillingAdmin = "RequireOrgBillingAdmin";

    private const string ShortRent = "RequireContext:short-rent:";
    private const string LongRent = "RequireContext:long-rent:";

    /// <summary>Either rental context: the permission may be held in short-rent or in long-rent (see <see cref="ParseContextPolicy"/>).</summary>
    private const string AnyRentalContext = "RequireContext:short-rent|long-rent:";

    /// <summary>Separates the alternative contexts of a context policy (<c>RequireContext:a|b:permission</c>).</summary>
    private const char ContextSeparator = '|';

    /// <summary>
    /// Read a property's short-stay side: pricing, iCal calendars, photos, CIN, listing activation, fiscal, service
    /// requests. Short-rent context only: a long-term landlord (long-rent <c>property.read</c>) does not pass.
    /// </summary>
    public const string PropertyRead = ShortRent + "property.read";

    /// <summary>Change a property's short-stay side (see <see cref="PropertyRead"/>). Short-rent context only.</summary>
    public const string PropertyWrite = ShortRent + "property.write";

    /// <summary>
    /// Read the property core that both kinds of landlord need (A7-06): the property list and record, its documents
    /// (APE) and the org plan entitlement. Passes with <c>property.read</c> in the short-rent <b>or</b> the long-rent
    /// context; the row is checked with <see cref="SharedPropertyOperations"/>.
    /// </summary>
    public const string SharedPropertyRead = AnyRentalContext + "property.read";

    /// <summary>Create or change the property core (record, documents): <c>property.write</c> in either rental context.</summary>
    public const string SharedPropertyWrite = AnyRentalContext + "property.write";

    public const string BookingRead = ShortRent + "booking.read";
    public const string BookingWrite = ShortRent + "booking.write";
    public const string PaymentRead = ShortRent + "payment.read";
    public const string PaymentWrite = ShortRent + "payment.write";
    public const string GuestRead = ShortRent + "guest.read";
    public const string GuestWrite = ShortRent + "guest.write";
    public const string OtaRead = ShortRent + "ota.read";
    public const string OtaWrite = ShortRent + "ota.write";

    /// <summary>
    /// Read a property's long-term side that is not a lease: the long-rent service requests (D2, SU-07). Long-rent
    /// context only, so a short-rent <c>property.read</c> never reaches it; the row is checked with
    /// <see cref="LongRentPropertyOperations"/>.
    /// </summary>
    public const string LongRentPropertyRead = LongRent + "property.read";

    /// <summary>Change a property's long-term side (see <see cref="LongRentPropertyRead"/>). Long-rent context only.</summary>
    public const string LongRentPropertyWrite = LongRent + "property.write";

    public const string LeaseRead = LongRent + "lease.read";
    public const string LeaseCreate = LongRent + "lease.create";
    public const string LeaseSign = LongRent + "lease.sign";
    public const string LeaseRegister = LongRent + "lease.register";

    /// <summary>Prefix of the context policies; the rest is <c>{context}:{permission}</c>.</summary>
    public const string ContextPolicyPrefix = "RequireContext:";

    /// <summary>The context policies registered at startup.</summary>
    public static IReadOnlyList<string> ContextPolicies { get; } =
    [
        PropertyRead, PropertyWrite,
        SharedPropertyRead, SharedPropertyWrite,
        BookingRead, BookingWrite,
        PaymentRead, PaymentWrite,
        GuestRead, GuestWrite,
        OtaRead, OtaWrite,
        LongRentPropertyRead, LongRentPropertyWrite,
        LeaseRead, LeaseCreate, LeaseSign, LeaseRegister,
    ];

    /// <summary>
    /// Splits a context policy name into its contexts and permission: <c>RequireContext:short-rent:property.read</c>
    /// has one context, <c>RequireContext:short-rent|long-rent:property.read</c> passes with either.
    /// </summary>
    public static (IReadOnlyList<string> ContextKeys, string PermissionKey) ParseContextPolicy(string policyName)
    {
        if (!policyName.StartsWith(ContextPolicyPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{policyName}' is not a context policy.", nameof(policyName));

        var parts = policyName[ContextPolicyPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"'{policyName}' is not a context policy.", nameof(policyName));

        var contextKeys = parts[0].Split(ContextSeparator);
        if (contextKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"'{policyName}' is not a context policy.", nameof(policyName));

        return (contextKeys, parts[1]);
    }
}
