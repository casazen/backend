using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class PublicHostResolver(IOrgService orgService, IOptions<PublicHostOptions> options) : IPublicHostResolver
{
    public async Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var normalizedHost = host.Trim().ToLowerInvariant();
        if (normalizedHost.Contains(':'))
            normalizedHost = normalizedHost.Split(':')[0];

        var slug = TryExtractCasazenSubdomainSlug(normalizedHost);
        if (slug is null)
            return null;

        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return null;

        return new ResolveHostResponseDto
        {
            OrgId = org.Id,
            Slug = org.Slug,
            PublicHostMode = PublicHostMode.CasazenSubdomain,
            Branding = PublicOrgDto.FromOrg(org),
        };
    }

    private string? TryExtractCasazenSubdomainSlug(string host)
    {
        var baseDomain = options.Value.BaseDomain.Trim().ToLowerInvariant();
        var suffix = $".{baseDomain}";
        if (!host.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        if (host.Equals(baseDomain, StringComparison.Ordinal) ||
            host.Equals($"www.{baseDomain}", StringComparison.Ordinal))
            return null;

        var subdomain = host[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(subdomain) || subdomain.Contains('.'))
            return null;

        if (options.Value.ReservedSubdomains.Any(r =>
                r.Equals(subdomain, StringComparison.OrdinalIgnoreCase)))
            return null;

        return subdomain;
    }
}
