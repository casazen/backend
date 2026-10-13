namespace Casazen.Core.Services;

/// <summary>
/// Whether an org is still "empty and unbilled" (AM-02, decision D14): the org a person got automatically when it first
/// signed in, before ever using it. Only such an org may be left behind when its person accepts an invitation to another
/// org; any other org stays where it is and the acceptance is refused (409 <c>invitation_user_has_organization</c>, no
/// merging of two orgs).
/// </summary>
/// <param name="IsEmpty">True when nothing stands in the way.</param>
/// <param name="Blockers">
/// Why it is not (stable codes, for the logs and the tests, never shown): <c>other_users</c>, <c>other_members</c>,
/// <c>data:{EntityName}</c> (a row in a tenant table), <c>plan</c>, <c>stripe_customer</c>, <c>subscription</c>,
/// <c>connect_account</c>, <c>domain</c>, <c>branding</c>, <c>not_a_host_org</c>, <c>not_found</c>. Empty when
/// <paramref name="IsEmpty"/>.
/// </param>
public sealed record OrgEmptiness(bool IsEmpty, IReadOnlyList<string> Blockers)
{
    public static OrgEmptiness Empty { get; } = new(true, []);

    public static OrgEmptiness Blocked(params string[] blockers) => new(false, blockers);
}

/// <summary>
/// Decides whether an org is empty and unbilled. The rules (all must hold):
/// <list type="bullet">
/// <item>the user is its only user and its only member (the owner);</item>
/// <item>no row in <b>any</b> tenant-owned table (<c>ITenantOwned</c>), soft-deleted properties included, except the rows the
/// onboarding itself leaves (<c>ConsentRecord</c>, <c>SignupAttribution</c>, <c>OrgSlugAlias</c>) and the user's own
/// <c>OrgMember</c> row: the list of tables is read from the model, so a table added tomorrow is checked from the first day
/// (a test fails otherwise);</item>
/// <item>the Starter plan with no Stripe customer, no subscription, no Connect account, no custom domain and no branding.</item>
/// </list>
/// </summary>
public interface IOrgEmptinessChecker
{
    /// <summary>
    /// The state of <paramref name="orgId"/> for <paramref name="userId"/>, who is expected to be its only user and its
    /// owner. Read without the tenant filter (the org is not necessarily the one of the request).
    /// </summary>
    Task<OrgEmptiness> CheckAsync(Guid orgId, string userId, CancellationToken cancellationToken = default);
}
