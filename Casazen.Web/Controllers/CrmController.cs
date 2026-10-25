using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// CRM guest profiles for the caller's org (NF-23, ADR-021): read aggregated guest data
/// for marketing and segmentation, scoped to the properties the caller reaches.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.GuestRead)]
public class CrmController(
    AppDbContext dbContext,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<CrmController> logger) : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxTagsPerGuest = 50;

    /// <summary>
    /// Get paginated list of CRM guest profiles, filtered by HostScope (ADR-021-R8):
    /// only profiles with stays at properties the caller reaches.
    /// </summary>
    [HttpGet("guests")]
    public async Task<ActionResult<PagedResultDto<GuestCrmProfileDto>>> GetGuests(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Retrieving CRM guest profiles with search term: {HasSearch}", !string.IsNullOrWhiteSpace(search));

        // Resolve caller's scope
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbid();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // Query profiles
        var query = dbContext.GuestCrmProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId);

        // ADR-021-R8: HostScope filtering - only profiles with stays at properties in scope
        if (!scope.IsOrgWide)
        {
            // Get accessible property IDs
            var accessiblePropertyIds = await dbContext.Properties
                .Where(prop => prop.OrgId == scope.OrgId)
                .InScope(scope)
                .Select(prop => prop.Id.ToString())
                .ToListAsync(cancellationToken);

            if (accessiblePropertyIds.Count == 0)
            {
                return Ok(new PagedResultDto<GuestCrmProfileDto>
                {
                    Items = [],
                    TotalCount = 0,
                    Page = page,
                    PageSize = pageSize,
                });
            }

            // Filter profiles that have stayed at accessible properties
            query = query.Where(p =>
                accessiblePropertyIds.Any(propId => p.PropertyIdsJson.Contains(propId)));
        }

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.Trim().ToLowerInvariant();
            query = query.Where(p =>
                p.ConfirmedMarketingEmail.ToLower().Contains(searchLower) ||
                p.Country.ToLower().Contains(searchLower));
        }

        var total = await query.CountAsync(cancellationToken);

        var profiles = await query
            .OrderByDescending(p => p.LastStayAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new PagedResultDto<GuestCrmProfileDto>
        {
            Items = profiles.Select(ToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    private Task<Guid?> ResolveOrgIdAsync(CancellationToken cancellationToken = default) =>
        orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);

    /// <summary>
    /// What the caller reaches in its org (AM-03): the whole org, or the properties it was given. <c>null</c> when it has no org yet
    /// or no reach on it; that is never widened to the whole org.
    /// </summary>
    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken = default)
    {
        var orgId = await ResolveOrgIdAsync(cancellationToken);
        return orgId is null
            ? null
            : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private static GuestCrmProfileDto ToDto(GuestCrmProfile profile)
    {
        var propertyIds = JsonSerializer.Deserialize<List<Guid>>(profile.PropertyIdsJson) ?? [];
        var tags = JsonSerializer.Deserialize<List<string>>(profile.TagsJson) ?? [];

        return new GuestCrmProfileDto
        {
            Id = profile.Id,
            ConfirmedMarketingEmail = profile.ConfirmedMarketingEmail,
            MarketingConsentEmail = profile.MarketingConsentEmail,
            MarketingConsentWhatsApp = profile.MarketingConsentWhatsApp,
            Language = profile.Language,
            Country = profile.Country,
            TotalStays = profile.TotalStays,
            TotalSpend = profile.TotalSpend,
            PropertyIds = propertyIds,
            Tags = tags,
            FirstStayAt = profile.FirstStayAt,
            LastStayAt = profile.LastStayAt,
        };
    }
}

public class GuestCrmProfileDto
{
    public Guid Id { get; set; }
    public string ConfirmedMarketingEmail { get; set; } = string.Empty;
    public bool MarketingConsentEmail { get; set; }
    public bool MarketingConsentWhatsApp { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public int TotalStays { get; set; }
    public decimal TotalSpend { get; set; }
    public List<Guid> PropertyIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public DateTime FirstStayAt { get; set; }
    public DateTime LastStayAt { get; set; }
}
