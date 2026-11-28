using Casazen.Core.Authorization;

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
    public const string Authenticated = "Authenticated";

    public const string AdminOnly = "AdminOnly";

    public const string Supplier = "RequireSupplier";

    /// <summary>
    /// Administrator of the caller's org, in whichever rental context it works (PL-16): plan, entitlement, billing,
    /// Connect, branding, domain, site documents and, since AM-03b, the export of the org's fiscal data and team
    /// (<c>GET api/gdpr/org/export</c>). An org policy, not a context one: the org's owner (<c>PropertyOwner</c>
    /// or <c>LongTermLandlord</c>, as JWT role or as DB membership of the owner's role key), an administrator of the org team
    /// or a platform admin; never a <c>Staff</c> collaborator, a <c>PropertyManager</c> (D12) or the holder of any other member
    /// role (<c>OrgBillingAdminAuthorizationHandler</c>, <see cref="Casazen.Core.Authorization.OrgOwnerRoles"/>).
    /// </summary>
    public const string OrgBillingAdmin = "RequireOrgBillingAdmin";

    public const string GuestSession = "GuestSession";

    /// <summary>
    /// A site connected to the org, its WordPress plugin (S022, PLG-BE-CONNECT, site integration plan §10.4): a valid, active credential of the scheme
    /// <c>SiteToken</c> (<c>Authorization: Bearer czs_…</c>, <c>SiteTokenAuthenticationHandler</c>) with its connection, its org and the scope
    /// <c>site:domain</c>. It is not a sign-in of a person and carries no role: it opens only <c>api/site/v1/*</c>, whose actions read the connection with
    /// <c>User.GetSiteConnection()</c> and act on its org only (never an org of the request). A sign-in of a person opens none of them (401).
    /// </summary>
    public const string SiteConnection = "SiteConnection";

    /// <summary>
    /// The person with a CasaZen account who books as a guest (the mobile app's guest area, <c>api/guest/*</c>): any signed-in account
    /// with a subject, whatever its role, and with <b>no host org and no supplier link needed</b>, because a guest has neither. It opens
    /// only the actions that read the caller's own bookings (the direct bookings whose guest has the account's verified email address, which
    /// the action checks and answers 403 <c>guest_email_not_verified</c> for) and the deletion of the account. Not a guest-session
    /// token (<see cref="GuestSession"/>, a code and an email with no account): that one is another scheme and opens none of these.
    /// </summary>
    public const string GuestAccount = "GuestAccount";

    /// <summary>
    /// Long-rent tenant self-service area: a signed-in account with a subject. The endpoint resolves the account's
    /// verified Auth0 email and grants access only when it matches a non-anonymized tenant party of the requested lease.
    /// A tenant is deliberately not made a member of the landlord's organization.
    /// </summary>
    public const string TenantAccount = "TenantAccount";

    private const string ShortRent = "RequireContext:short-rent:";
    private const string LongRent = "RequireContext:long-rent:";

    private const string AnyRentalContext = "RequireContext:short-rent|long-rent:";

    private const char ContextSeparator = '|';

    /// <summary>
    /// Read a property's short-stay side: pricing, iCal calendars, photos, CIN, listing activation, fiscal, service
    /// requests. Short-rent context only: a long-term landlord (long-rent <c>property.read</c>) does not pass.
    /// </summary>
    public const string PropertyRead = ShortRent + "property.read";

    public const string PropertyWrite = ShortRent + "property.write";

    public const string SharedPropertyRead = AnyRentalContext + "property.read";

    public const string SharedPropertyWrite = AnyRentalContext + "property.write";

    public const string BookingRead = ShortRent + "booking.read";
    public const string BookingWrite = ShortRent + "booking.write";
    public const string PaymentRead = ShortRent + "payment.read";
    public const string PaymentWrite = ShortRent + "payment.write";
    public const string GuestRead = ShortRent + "guest.read";
    public const string Marketing = ShortRent + HostPermissions.Marketing;
    public const string GuestWrite = ShortRent + "guest.write";
    public const string OtaRead = ShortRent + "ota.read";
    public const string OtaWrite = ShortRent + "ota.write";

    // The finer permissions of AM-03, carved out of the broad ones so that the collaborator can do its job without prices, CIN
    // or bookings (HostPermissions).

    public const string ServiceRequestWrite = ShortRent + HostPermissions.ServiceRequestWrite;

    public const string GuestManage = ShortRent + HostPermissions.GuestManage;

    /// <summary>
    /// Register the guests of a stay and declare the Alloggiati communication sent. It used to be <see cref="BookingWrite"/>,
    /// which still creates, moves and cancels bookings. Short-rent context.
    /// </summary>
    public const string AlloggiatiSubmit = ShortRent + HostPermissions.AlloggiatiSubmit;

    /// <summary>
    /// Decide on manual biometric verification (NF-12, ADR-013): approve or reject guest check-in after biometric
    /// escalation. Owner, admins, and property managers only (NOT staff per D-08). Short-rent context. The permission is
    /// <see cref="HostPermissions.VerificationDecision"/>, seeded since E03/F04/US02/T01.
    /// </summary>
    public const string VerificationDecision = ShortRent + HostPermissions.VerificationDecision;

    public const string ApprovalDecide = ShortRent + HostPermissions.ApprovalDecide;

    public const string ApprovalQueueAccess = "ApprovalQueueAccess";
    public static IReadOnlyList<string> ApprovalQueuePolicies { get; } = [ApprovalDecide, Marketing];

    public const string GuestPortalManage = ShortRent + HostPermissions.GuestPortalManage;

    public const string LongRentPropertyRead = LongRent + "property.read";

    public const string LongRentPropertyWrite = LongRent + "property.write";

    public const string LeaseRead = LongRent + "lease.read";
    public const string LeaseCreate = LongRent + "lease.create";
    public const string LeaseSign = LongRent + "lease.sign";
    public const string LeaseRegister = LongRent + "lease.register";

    private const string Agency = "RequireAgencyTier:";

    public const string AgencyOwnerView = Agency + AgencyPermissions.OwnerView;

    public const string AgencyOwnerFinancialRead = Agency + AgencyPermissions.OwnerFinancialRead;

    public const string AgencyStatementManage = Agency + AgencyPermissions.StatementManage;

    public const string AgencyStatementView = Agency + AgencyPermissions.StatementView;

    public const string AgencyInvoiceManage = Agency + AgencyPermissions.InvoiceManage;

    public const string AgencyWithholdingManage = Agency + AgencyPermissions.WithholdingManage;

    public const string AgencyWireManage = Agency + AgencyPermissions.WireManage;

    public const string AgencyActivityManage = Agency + AgencyPermissions.ActivityManage;

    public const string TrustedSupplierManage = "host.trusted-supplier.manage";

    public const string AgencyOwnerManage = Agency + AgencyPermissions.OwnerManage;

    public const string AgencyOwnerFinancialManage = Agency + AgencyPermissions.OwnerFinancialManage;

    public const string AgencyStatementPublish = Agency + AgencyPermissions.StatementPublish;


    /// <summary>
    /// Owner portal access (ADR-011): checks JWT claim context=owner_portal and resolves OwnerUserId.
    /// MUST NOT include host roles. Enforced on every owner portal endpoint.
    /// </summary>
    public const string OwnerPortal = "OwnerPortal";

    public const string OrgMembersManage = ContextPolicyPrefix + AccountContext.Key + ":" + AccountContext.Permissions.MembersManage;

    public const string OrgActivityRead = ContextPolicyPrefix + AccountContext.Key + ":" + AccountContext.Permissions.ActivityRead;

    public const string ContextPolicyPrefix = "RequireContext:";

    public static IReadOnlyList<string> ContextPolicies { get; } =
    [
        PropertyRead, PropertyWrite,
        SharedPropertyRead, SharedPropertyWrite,
        BookingRead, BookingWrite,
        PaymentRead, PaymentWrite,
        GuestRead, GuestWrite, Marketing,
        OtaRead, OtaWrite,
        ServiceRequestWrite, GuestManage, AlloggiatiSubmit,
        VerificationDecision,
        ApprovalDecide,
        GuestPortalManage,
        LongRentPropertyRead, LongRentPropertyWrite,
        LeaseRead, LeaseCreate, LeaseSign, LeaseRegister,
        OrgMembersManage, OrgActivityRead,
    ];

    public static IReadOnlyList<string> AgencyPolicies { get; } =
    [
        AgencyOwnerView,
        AgencyOwnerFinancialRead,
        AgencyOwnerManage,
        AgencyStatementManage,
        AgencyStatementView,
        AgencyInvoiceManage,
        AgencyWithholdingManage,
        AgencyWireManage,
        AgencyActivityManage,
        AgencyOwnerFinancialManage,
        AgencyStatementPublish,
    ];

    public static string ParseAgencyPolicy(string policyName)
    {
        if (!policyName.StartsWith(Agency, StringComparison.Ordinal) || policyName.Length == Agency.Length)
            throw new ArgumentException($"'{policyName}' is not an agency policy.", nameof(policyName));

        return policyName[Agency.Length..];
    }

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
