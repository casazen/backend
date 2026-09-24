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

    /// <summary>
    /// Read a property and its configuration. <c>property.*</c> is shared by the short-rent and long-rent contexts
    /// (<c>ContextAuthorizationService</c>), so this policy admits both kinds of host.
    /// </summary>
    public const string PropertyRead = ShortRent + "property.read";

    /// <summary>Change a property and its configuration (pricing, documents, service requests). Both host contexts.</summary>
    public const string PropertyWrite = ShortRent + "property.write";

    public const string BookingRead = ShortRent + "booking.read";
    public const string BookingWrite = ShortRent + "booking.write";
    public const string PaymentRead = ShortRent + "payment.read";
    public const string PaymentWrite = ShortRent + "payment.write";
    public const string GuestRead = ShortRent + "guest.read";
    public const string GuestWrite = ShortRent + "guest.write";
    public const string OtaRead = ShortRent + "ota.read";
    public const string OtaWrite = ShortRent + "ota.write";

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
        BookingRead, BookingWrite,
        PaymentRead, PaymentWrite,
        GuestRead, GuestWrite,
        OtaRead, OtaWrite,
        LeaseRead, LeaseCreate, LeaseSign, LeaseRegister,
    ];

    /// <summary>Splits a context policy name into its context and permission.</summary>
    public static (string ContextKey, string PermissionKey) ParseContextPolicy(string policyName)
    {
        if (!policyName.StartsWith(ContextPolicyPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{policyName}' is not a context policy.", nameof(policyName));

        var parts = policyName[ContextPolicyPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"'{policyName}' is not a context policy.", nameof(policyName));

        return (parts[0], parts[1]);
    }
}
