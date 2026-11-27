using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.SiteConnections;

/// <summary>
/// The account of a connected plugin (S023, PLG-BE-SITEAPI, site integration plan §10.3): <c>GET api/site/v1/account</c>, and the whole answer of <c>PUT domain</c>,
/// <c>PUT domain/surfaces</c> and (under <c>account</c>) <c>POST domain/verify</c>. <see cref="Domain"/> is the object <c>GET api/orgs/{orgId}/domain</c> gives the
/// console, member by member (<see cref="OrgDomainMapper"/>).
/// </summary>
/// <param name="ConnectionId">The connection of the credential that asked.</param>
/// <param name="Org">The org of the credential (the same object the exchange of the code answered).</param>
/// <param name="EmbedEnabled">What <c>resolve-host</c> answers for the org: the embeddable pages are on.</param>
/// <param name="PublicAddress">The origin where <c>/embed/v1/manifest.json</c> and <c>resolve-host</c> answer for the org: its own domain when it is served now, else the one of CasaZen.</param>
/// <param name="PublicAddressKind"><see cref="CustomDomainKind"/> (the plugin leaves the slug empty) or <see cref="CasazenKind"/> (the plugin keeps the slug of the org).</param>
/// <param name="Domain">The domain settings of the org, as the console gets them.</param>
public sealed record SiteAccountDto(
    Guid ConnectionId,
    SiteConnectionOrgDto Org,
    bool EmbedEnabled,
    string PublicAddress,
    string PublicAddressKind,
    OrgDomainConfigDto Domain)
{
    public const string CustomDomainKind = "customDomain";

    public const string CasazenKind = "casazen";

    public static SiteAccountDto From(SiteAccount account, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new SiteAccountDto(
            account.ConnectionId,
            SiteConnectionOrgDto.From(account.Org),
            account.EmbedEnabled,
            account.PublicAddress,
            account.PublicAddressKind == SitePublicAddressKind.CustomDomain ? CustomDomainKind : CasazenKind,
            OrgDomainMapper.ToDto(account.Domain, localizer));
    }
}

public sealed record SiteDomainVerifyResponse(OrgDomainVerifyResultDto Verify, SiteAccountDto Account);

/// <summary>
/// Body of <c>PUT api/site/v1/domain</c> (S023): <c>{ "customDomain": "prenota.example.com" }</c> serves the org on that domain, <c>{ "customDomain": null }</c> takes it
/// back to the address of CasaZen. The member is <b>required</b>, <c>null</c> included: a body without it (a client that dropped its empty values) changes nothing and is
/// a 400, never read as «go back to CasaZen». Nothing else is read: an org id, whatever the body says, is ignored (the org is the credential's).
/// </summary>
public sealed class SetSiteDomainRequest
{
    private string? _customDomain;

    [StringLength(253)]
    public string? CustomDomain
    {
        get => _customDomain;
        set
        {
            _customDomain = value;
            CustomDomainSpecified = true;
        }
    }

    [JsonIgnore]
    public bool CustomDomainSpecified { get; private set; }
}
