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
    /// <summary>Any signed-in user, suppliers included. User-scoped endpoints only (see remarks).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Platform administrator (JWT role <c>Admin</c>).</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Supplier console (JWT role <c>Supplier</c>, backfilled from the DB supplier link).</summary>
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

    /// <summary>
    /// The guest of a booking site, with no account (DB-06a): a valid, unexpired guest-session token (<c>Authorization: Bearer gs1.…</c>,
    /// <c>IGuestSessionService</c>). It is not a sign-in of the hosts and carries no role: it opens only the actions of the guest area
    /// (<c>api/public/guest/*</c>), which read the ticket with <c>User.GetGuestSessionTicket()</c> and find the guest's own bookings with
    /// <c>IGuestSessionService.FindBookingAsync</c>. The request is scoped to the org of the session.
    /// </summary>
    public const string GuestSession = "GuestSession";

    /// <summary>
    /// The person with a CasaZen account who books as a guest (the mobile app's guest area, <c>api/guest/*</c>): any signed-in account
    /// with a subject, whatever its role, and with <b>no host org and no supplier link needed</b>, because a guest has neither. It opens
    /// only the actions that read the caller's own bookings (the direct bookings whose guest has the account's verified email address, which
    /// the action checks and answers 403 <c>guest_email_not_verified</c> for) and the deletion of the account. Not a guest-session
    /// token (<see cref="GuestSession"/>, a code and an email with no account): that one is another scheme and opens none of these.
    /// </summary>
    public const string GuestAccount = "GuestAccount";

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
    /// Read the property core that both kinds of landlord need (A7-06): the property list and record and its documents
    /// (APE). Passes with <c>property.read</c> in the short-rent <b>or</b> the long-rent context; the row is checked with
    /// <see cref="SharedPropertyOperations"/>. The org plan entitlement is an org matter: <see cref="OrgBillingAdmin"/>.
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

    // The finer permissions of AM-03, carved out of the broad ones so that the collaborator can do its job without prices, CIN
    // or bookings (HostPermissions).

    /// <summary>
    /// Ask a supplier for an intervention on a stay (find the supplier, create the request, mark it paid). It used to be
    /// <see cref="PropertyWrite"/>. Short-rent context.
    /// </summary>
    public const string ServiceRequestWrite = ShortRent + HostPermissions.ServiceRequestWrite;

    /// <summary>
    /// Erase or anonymize a guest and change its consents: the destructive and legal acts on a guest's data. It used to be
    /// <see cref="GuestWrite"/>, which still lets a person register and correct a guest. Short-rent context.
    /// </summary>
    public const string GuestManage = ShortRent + HostPermissions.GuestManage;

    /// <summary>
    /// Register the guests of a stay and declare the Alloggiati communication sent. It used to be <see cref="BookingWrite"/>,
    /// which still creates, moves and cancels bookings. Short-rent context.
    /// </summary>
    public const string AlloggiatiSubmit = ShortRent + HostPermissions.AlloggiatiSubmit;

    /// <summary>
    /// Decide on manual biometric verification (NF-12, ADR-013): approve or reject guest check-in after biometric
    /// escalation. Owner, admins, and property managers only (NOT staff per D-08). Short-rent context.
    /// </summary>
    public const string VerificationDecision = ShortRent + "verification.decision";

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

    /// <summary>
    /// Agency tier B2B policies (ADR-010, ADR-012): each requires the agency plan (<c>Org.PlanTier == PlanTier.Agency</c>, reads only
    /// once the org is downgraded) <b>and</b> its agency permission held by the caller's org role (DEB-01,
    /// <see cref="OrgRoleCatalog.AgencyPermissionsOf"/>): the rest of the name is the permission (<see cref="ParseAgencyPolicy"/>).
    /// </summary>
    private const string Agency = "RequireAgencyTier:";

    /// <summary>View property owner list and details, IBAN masked (agency B2B, ADR-012).</summary>
    public const string AgencyOwnerView = Agency + AgencyPermissions.OwnerView;

    /// <summary>View full IBAN of property owners (requires audit log, agency B2B, ADR-012-R3).</summary>
    public const string AgencyOwnerFinancialRead = Agency + AgencyPermissions.OwnerFinancialRead;

    /// <summary>Generate, approve and publish monthly statements (agency B2B).</summary>
    public const string AgencyStatementManage = Agency + AgencyPermissions.StatementManage;

    /// <summary>View and export monthly statements (agency B2B).</summary>
    public const string AgencyStatementView = Agency + AgencyPermissions.StatementView;

    /// <summary>Create and issue invoices (agency B2B).</summary>
    public const string AgencyInvoiceManage = Agency + AgencyPermissions.InvoiceManage;

    /// <summary>Manage withholding tax (agency B2B).</summary>
    public const string AgencyWithholdingManage = Agency + AgencyPermissions.WithholdingManage;

    /// <summary>Create wire transfer batches (agency B2B).</summary>
    public const string AgencyWireManage = Agency + AgencyPermissions.WireManage;

    /// <summary>Manage internal staff activities (agency B2B).</summary>
    public const string AgencyActivityManage = Agency + AgencyPermissions.ActivityManage;

    /// <summary>Manage trusted suppliers (agency B2B).</summary>
    public const string AgencySupplierManage = Agency + AgencyPermissions.SupplierManage;

    /// <summary>Invite and manage property owners (agency B2B, ADR-011-R4).</summary>
    public const string AgencyOwnerManage = Agency + AgencyPermissions.OwnerManage;

    /// <summary>
    /// Owner portal access (ADR-011): checks JWT claim context=owner_portal and resolves OwnerUserId.
    /// MUST NOT include host roles. Enforced on every owner portal endpoint.
    /// </summary>
    public const string OwnerPortal = "OwnerPortal";

    /// <summary>
    /// Invite, change, deactivate, reactivate and remove the people of the caller's org (AM-02): the permission
    /// <c>org.members.manage</c> of the <c>account</c> context, held by the org's owner and administrators and by nobody
    /// else. Like the host contexts it waits for the onboarding and the consents. What each of them may do to whom
    /// (only the owner creates and manages administrators) is decided by the services (<c>OrgTeamRules</c>).
    /// </summary>
    public const string OrgMembersManage = ContextPolicyPrefix + AccountContext.Key + ":" + AccountContext.Permissions.MembersManage;

    /// <summary>
    /// Read the activity log of the caller's org (AM-02b): the list and the CSV of who did what. The permission
    /// <c>org.activity.read</c> of the <c>account</c> context, held by the org's owner and administrators and by nobody else
    /// (AM-01 seeds it). Like the host contexts it waits for the onboarding and the consents.
    /// </summary>
    public const string OrgActivityRead = ContextPolicyPrefix + AccountContext.Key + ":" + AccountContext.Permissions.ActivityRead;

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
        ServiceRequestWrite, GuestManage, AlloggiatiSubmit,
        VerificationDecision,
        LongRentPropertyRead, LongRentPropertyWrite,
        LeaseRead, LeaseCreate, LeaseSign, LeaseRegister,
        OrgMembersManage, OrgActivityRead,
    ];

    /// <summary>The agency tier policies registered at startup (ADR-010, ADR-012): one per agency permission.</summary>
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
        AgencySupplierManage,
    ];

    /// <summary>The agency permission an agency policy requires: <c>RequireAgencyTier:agency.wire.manage</c> needs <c>agency.wire.manage</c>.</summary>
    public static string ParseAgencyPolicy(string policyName)
    {
        if (!policyName.StartsWith(Agency, StringComparison.Ordinal) || policyName.Length == Agency.Length)
            throw new ArgumentException($"'{policyName}' is not an agency policy.", nameof(policyName));

        return policyName[Agency.Length..];
    }

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
