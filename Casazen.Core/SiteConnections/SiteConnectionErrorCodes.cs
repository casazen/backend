namespace Casazen.Core.SiteConnections;

/// <summary>
/// The stable <c>code</c> of the errors of the connection of a site (S022, PLG-BE-CONNECT, site integration plan §10.3), answered through <c>ApiProblem</c>
/// with their texts in <c>SharedResources</c> (IT and EN).
/// </summary>
public static class SiteConnectionErrorCodes
{
    /// <summary>400: a request of authorization that breaks any rule of <see cref="SiteConnectionRules"/> (the same answer whatever the rule).</summary>
    public const string InvalidRequest = "site_connection_invalid_request";

    /// <summary>400: an exchange of a code that does not give a credential (unknown, expired, used, wrong verifier, another site): one answer, no detail.</summary>
    public const string InvalidGrant = "site_connection_invalid_grant";

    /// <summary>404: a connected site that is not one of the org.</summary>
    public const string NotFound = "site_connection_not_found";

    /// <summary>
    /// 503: the account of the plugin (S023) has no address to give: the org is not on a served domain of its own and <c>App:PublicSiteBaseUrl</c> is not configured
    /// (Development and Testing only: elsewhere the startup fails). Never a wrong address.
    /// </summary>
    public const string PublicAddressNotConfigured = "site_public_address_not_configured";
}
