using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Crm;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// CRM guest profiles for the caller's org (NF-23, NF-88, ADR-021): the list with search and filters, the detail of a guest with its history,
/// totals, reviews and contacts, and the tags (E07/F01/US03). A thin controller over <see cref="ICrmProfileReadService"/>, which decides the
/// scope: a caller sees the profiles that have a booking at a property it reaches, and the figures it sees are computed over those bookings; a
/// profile of another org, or one it does not reach, is a 404 like a missing one.
/// Behind <c>Features:CrmMarketing</c> (E07/F06/US01/T01): 404 while it is off, before authentication.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.GuestRead)]
public class CrmController(
    ICrmProfileReadService profiles,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<CrmController> logger) : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = CrmProfileRules.MaxPageSize;
    public const int MaxTagsPerGuest = CrmProfileRules.MaxTags;

    /// <summary>404: the profile does not exist, is of another org, is not reached by the caller or was merged into another.</summary>
    public const string ProfileNotFoundCode = "crm_profile_not_found";

    /// <summary>
    /// The profiles of the caller's org that have a booking at a property the caller reaches, filtered and sorted (ADR-021-R8, AC-NF88-5). A
    /// caller with no reach gets 403 and is never widened to the whole org.
    /// </summary>
    /// <param name="search">A part of the name of the guest, or an exact e-mail address or phone number.</param>
    /// <param name="country">The country of the profile.</param>
    /// <param name="language">The language of the profile (<c>it</c>, <c>en</c>).</param>
    /// <param name="channel">The channel the guest booked on: <c>Direct</c>, <c>Airbnb</c>, <c>BookingCom</c>...</param>
    /// <param name="tag">A tag of the profile.</param>
    /// <param name="minStays">At least this many stays.</param>
    /// <param name="reachableByEmail"><c>true</c>: with an e-mail address that may receive marketing; <c>false</c>: without.</param>
    /// <param name="sort"><c>lastStay</c> (default), <c>spend</c>, <c>stays</c> or <c>name</c>.</param>
    /// <param name="page">From 1.</param>
    /// <param name="pageSize">At most 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("guests")]
    [ProducesResponseType(typeof(PagedResultDto<CrmProfileListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<CrmProfileListItemDto>>> GetGuests(
        [FromQuery] string? search,
        [FromQuery] string? country,
        [FromQuery] string? language,
        [FromQuery] string? channel,
        [FromQuery] string? tag,
        [FromQuery] int? minStays,
        [FromQuery] bool? reachableByEmail,
        [FromQuery] CrmProfileSort sort = CrmProfileSort.LastStay,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Retrieving CRM guest profiles: search {HasSearch}, sort {Sort}", !string.IsNullOrWhiteSpace(search), sort);

        // No org yet, or no reach on it: nothing is listed and nothing is widened to the whole org.
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbid();

        var filter = new CrmProfileFilter(search, country, language, channel, tag, minStays, reachableByEmail, sort);
        var result = await profiles.ListAsync(scope, filter, page, pageSize, cancellationToken);

        return Ok(new PagedResultDto<CrmProfileListItemDto>
        {
            Items = result.Items.Select(CrmProfileListItemDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>
    /// A profile in detail: who it is, the stays the caller reaches with their channel and amount, the totals (spend per channel, stays per
    /// property, the typical group, the booking advance), the reviews, the tags and the contacts with the reason why one is not marketable
    /// (AC-NF88-4). A profile of another org, or one the caller does not reach, is 404 <c>crm_profile_not_found</c>.
    /// </summary>
    /// <param name="id">The profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("guests/{id:guid}")]
    [ProducesResponseType(typeof(CrmProfileDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CrmProfileDetailDto>> GetGuest(Guid id, CancellationToken cancellationToken = default)
    {
        if (await ResolveScopeAsync(cancellationToken) is not { } scope)
            return ProfileNotFound();

        var detail = await profiles.GetAsync(scope, id, cancellationToken);
        return Ok(CrmProfileDetailDto.From(detail));
    }

    /// <summary>
    /// Replaces the tags of a profile: at most 50, each 1 to 40 characters, no two alike (422 <c>crm_tags_invalid</c> otherwise). A profile of
    /// another org, or one the caller does not reach, is 404 <c>crm_profile_not_found</c>.
    /// </summary>
    /// <param name="id">The profile.</param>
    /// <param name="request">The tags.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("guests/{id:guid}/tags")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetTags(Guid id, [FromBody] SetCrmTagsRequest request, CancellationToken cancellationToken = default)
    {
        if (await ResolveScopeAsync(cancellationToken) is not { } scope)
            return ProfileNotFound();

        await profiles.SetTagsAsync(scope, id, request.Tags, cancellationToken);

        logger.LogInformation("User {UserId} replaced the tags of CRM profile {ProfileId}", User.GetUserId(), id);
        return NoContent();
    }

    /// <summary>
    /// What the caller reaches in its org (AM-03): the whole org, or the properties it was given. <c>null</c> when it has no org yet or no
    /// reach on it; that is never widened to the whole org.
    /// </summary>
    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null
            ? null
            : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private ObjectResult ProfileNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProfileNotFoundCode, "CrmProfileNotFound");
}
