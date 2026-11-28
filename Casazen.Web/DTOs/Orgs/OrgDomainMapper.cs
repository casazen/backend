using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.Orgs;

public static class OrgDomainMapper
{
    public static OrgDomainConfigDto ToDto(OrgDomainConfig config, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(localizer);

        return new OrgDomainConfigDto
        {
            OrgId = config.OrgId,
            PublicHostMode = config.PublicHostMode,
            Subdomain = config.Subdomain,
            CustomDomain = config.CustomDomain,
            DomainVerificationStatus = config.DomainVerificationStatus,
            CanUseCustomDomain = config.CanUseCustomDomain,
            DnsInstructions = config.DnsInstructions is null
                ? null
                : new DnsInstructionsDto
                {
                    CnameHost = config.DnsInstructions.CnameHost,
                    CnameTarget = config.DnsInstructions.CnameTarget,
                    TxtHost = config.DnsInstructions.TxtHost,
                    TxtValue = config.DnsInstructions.TxtValue,
                    SslNote = config.DnsInstructions.SslNote,
                    ARecordValues = config.DnsInstructions.ARecordValues,
                    VercelTxtHost = config.DnsInstructions.VercelTxtHost,
                    VercelTxtValue = config.DnsInstructions.VercelTxtValue,
                },
            PublicUrls = new PublicUrlsDto
            {
                PathUrl = config.PublicUrls.PathUrl,
                SubdomainUrl = config.PublicUrls.SubdomainUrl,
                CustomDomainUrl = config.PublicUrls.CustomDomainUrl,
            },
            Status = new DomainStatusDto
            {
                Detail = config.Status.Detail,
                Message = Explain(config.Status.Detail, localizer),
                CheckedAt = config.Status.CheckedAt,
                VerifiedAt = config.Status.VerifiedAt,
                ActivationAvailable = config.Status.ActivationAvailable,
                AutoCheckActive = config.Status.AutoCheckActive,
            },
            Surfaces = config.Surfaces is not { } surfaces
                ? null
                : new OrgDomainSurfacesDto
                {
                    GuestAreaOnOwnDomain = surfaces.GuestAreaOnOwnDomain,
                    OwnerPortalOnOwnDomain = surfaces.OwnerPortalOnOwnDomain,
                    UseOwnDomainForLinks = surfaces.UseOwnDomainForLinks,
                    OwnerLoginHostState = surfaces.OwnerLoginHostState,
                    OwnerLoginAutomatic = surfaces.OwnerLoginAutomatic,
                    Effective = new OrgDomainEffectiveSurfacesDto
                    {
                        GuestArea = surfaces.Effective.GuestArea,
                        OwnerPortal = surfaces.Effective.OwnerPortal,
                        OwnerLoginReady = surfaces.Effective.OwnerLoginReady,
                        LinksOnOwnDomain = surfaces.Effective.GuestLinksOnOwnDomain || surfaces.Effective.OwnerLinksOnOwnDomain,
                    },
                },
            Tracking = config.Tracking is not { } tracking
                ? null
                : new OrgDomainTrackingDto { GtmContainerId = tracking.GtmContainerId },
        };
    }

    public static OrgDomainVerifyResultDto ToDto(DomainVerificationResult verification, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(localizer);

        return new OrgDomainVerifyResultDto
        {
            DomainVerificationStatus = verification.Status,
            CustomDomain = verification.CustomDomain,
            CheckedAt = verification.CheckedAt,
            Detail = verification.Detail,
            Message = Explain(verification.Detail, localizer),
            VercelTxtHost = verification.VercelTxtHost,
            VercelTxtValue = verification.VercelTxtValue,
        };
    }

    public static string? Explain(string? detail, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (string.IsNullOrEmpty(detail))
            return null;

        var text = localizer[$"DomainIssue_{detail}"];
        return text.ResourceNotFound ? null : text.Value;
    }
}
