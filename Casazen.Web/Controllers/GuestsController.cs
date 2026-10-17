using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Guests of the caller's org (TN-1). Every action is scoped to the caller's org: a guest of another
/// org answers 404, exactly like a missing one. Since SR-04b it is also scoped to the properties the caller reaches (AM-03): a
/// caller limited to some properties reaches only the guests that have a stay at one of them, with their document number and
/// scan, and any other guest answers 404 too; a guest with no stay at all is reached only by who reaches the whole org.
/// Responses are DTOs, never the entity.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.GuestRead)]
public class GuestsController(
    IGuestService guestService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IFileStorage fileStorage,
    IAuthorizationService authorizationService,
    ILogger<GuestsController> logger) : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>404: the guest has no document scan, or its file is not in the storage.</summary>
    public const string DocumentScanMissingCode = "guest_document_scan_missing";

    /// <summary>404: the guest has no document number (<c>GET /api/guests/{id}/document-number</c>).</summary>
    public const string DocumentNumberMissingCode = "guest_document_number_missing";

    /// <summary>Prefix of the private storage keys of guest document scans (<see cref="StorageKeys.GuestDocument"/>).</summary>
    private const string GuestDocumentKeyPrefix = "guest-documents/";

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<GuestSummaryDto>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        logger.LogInformation("Retrieving guests with search term: {HasSearch}", !string.IsNullOrWhiteSpace(search));

        // No org yet, or no reach on it: nothing is listed and nothing is widened to the whole org.
        var scope = await ResolveScopeAsync();
        if (scope is null)
            return Forbid();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (guests, total) = await guestService.GetGuestsPageAsync(
            scope, search, page, pageSize, HttpContext.RequestAborted);

        return Ok(new PagedResultDto<GuestSummaryDto>
        {
            Items = guests.Select(GuestDtoMapper.ToSummary).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GuestDto>> GetById(Guid id)
    {
        logger.LogInformation("Retrieving guest: {GuestId}", id);

        var guest = await FindGuestAsync(id);
        if (guest is null)
            return GuestNotFound();

        return Ok(GuestDtoMapper.ToDto(guest));
    }

    /// <summary>
    /// Full document number of a guest (CO-14): every other answer shows it masked (<c>*****</c> plus the last 3
    /// characters). Explicit action like the stay guests' one (CO-09): guest of the caller's org that the caller reaches (another
    /// org's guest, or one with no stay at a property the caller reaches, answers 404), <c>guest.read</c> on it (TN-3, otherwise
    /// 403), never cached; every request is logged with the user and the guest (audit), never with the number. 404
    /// <c>guest_document_number_missing</c> when there is none.
    /// </summary>
    [HttpGet("{id:guid}/document-number")]
    [ProducesResponseType(typeof(GuestDocumentNumberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GuestDocumentNumberDto>> GetDocumentNumber(Guid id)
    {
        var guest = await FindGuestAsync(id);
        if (guest is null)
            return GuestNotFound();

        if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForOrg(guest.OrgId), GuestOperations.Read))
        {
            logger.LogWarning("User {UserId} denied the document number of guest {GuestId}", User.GetUserId(), id);
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(guest.DocumentNumber))
            return this.ApiProblem(StatusCodes.Status404NotFound, DocumentNumberMissingCode, "GuestDocumentNumberMissing");

        logger.LogInformation("User {UserId} viewed the document number of guest {GuestId}", User.GetUserId(), id);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new GuestDocumentNumberDto { GuestId = guest.Id, DocumentNumber = guest.DocumentNumber.Trim() });
    }

    /// <summary>
    /// Downloads the identity document scan the guest uploaded (FD-07 open point, CO-09). The scan lives in the private
    /// bucket and is read only here: guest of the caller's org that the caller reaches (another org's guest, or one with no stay
    /// at a property the caller reaches, answers 404), <c>guest.read</c> on it (TN-3, otherwise 403), never cached; every download
    /// is logged with the user and the guest (audit).
    /// </summary>
    [HttpGet("{id:guid}/document-scan")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadDocumentScan(Guid id)
    {
        var guest = await FindGuestAsync(id);
        if (guest is null)
            return GuestNotFound();

        if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForOrg(guest.OrgId), GuestOperations.Read))
        {
            logger.LogWarning("User {UserId} denied the document scan of guest {GuestId}", User.GetUserId(), id);
            return Forbid();
        }

        var key = guest.DocumentScanUrl;
        // Only keys of the private bucket: a legacy "/uploads/..." path not migrated yet is never read from disk.
        if (!StorageKeys.IsValid(key) || !key!.StartsWith(GuestDocumentKeyPrefix, StringComparison.Ordinal))
            return this.ApiProblem(StatusCodes.Status404NotFound, DocumentScanMissingCode, "GuestDocumentScanMissing");

        var content = await fileStorage.OpenReadAsync(StorageBucket.Private, key, HttpContext.RequestAborted);
        if (content is null)
        {
            logger.LogWarning("Stored document scan missing for guest {GuestId}", id);
            return this.ApiProblem(StatusCodes.Status404NotFound, DocumentScanMissingCode, "GuestDocumentScanMissing");
        }

        logger.LogInformation("User {UserId} downloaded the document scan of guest {GuestId}", User.GetUserId(), id);
        Response.Headers.CacheControl = "private, no-store";
        var extension = Path.GetExtension(key).ToLowerInvariant();
        return File(content, StorageKeys.ContentTypeFor(key), $"documento-ospite{extension}");
    }

    [HttpGet("email/{email}")]
    public async Task<ActionResult<GuestDto>> GetByEmail(string email)
    {
        logger.LogInformation("Retrieving guest by email lookup");

        var scope = await ResolveScopeAsync();
        if (scope is null)
            return GuestNotFound();

        var guest = await guestService.GetGuestByEmailAsync(scope, email, HttpContext.RequestAborted);
        if (guest is null)
        {
            logger.LogWarning("Guest not found for email lookup");
            return GuestNotFound();
        }

        return Ok(GuestDtoMapper.ToDto(guest));
    }

    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<ActionResult<GuestDto>> Create([FromBody] CreateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        logger.LogInformation("Creating guest");

        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return Forbid();

        var guest = new Guest
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber ?? string.Empty,
            Address = request.Address ?? string.Empty,
            City = request.City ?? string.Empty,
            PostalCode = request.PostalCode ?? string.Empty,
            Country = request.Country ?? string.Empty,
            Notes = request.Notes ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // A duplicate e-mail in the same org raises DomainConflictException (409 guest_email_exists);
        // the same e-mail in another org is a different guest and never surfaces here.
        var created = await guestService.CreateGuestAsync(orgId.Value, guest, HttpContext.RequestAborted);
        logger.LogInformation("Guest created: {GuestId}", created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, GuestDtoMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<ActionResult<GuestDto>> Update(Guid id, [FromBody] UpdateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        logger.LogInformation("Updating guest: {GuestId}", id);

        var existing = await FindGuestAsync(id);
        if (existing is null)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return GuestNotFound();
        }

        existing.FirstName = request.FirstName;
        existing.LastName = request.LastName;
        existing.Email = request.Email;
        existing.PhoneNumber = request.PhoneNumber ?? string.Empty;
        existing.Address = request.Address ?? string.Empty;
        existing.City = request.City ?? string.Empty;
        existing.PostalCode = request.PostalCode ?? string.Empty;
        existing.Country = request.Country ?? string.Empty;
        existing.Notes = request.Notes ?? string.Empty;

        var updated = await guestService.UpdateGuestAsync(existing);
        logger.LogInformation("Guest updated: {GuestId}", updated.Id);
        return Ok(GuestDtoMapper.ToDto(updated));
    }

    /// <summary>
    /// Deletes a guest of the caller's org. Without bookings the row is removed; with past bookings it is
    /// kept (Restrict FK) but marked deleted and anonymized; with an open booking the answer is
    /// 409 <c>guest_has_open_bookings</c>. Never a 500 for the foreign key (A9-11). It needs <c>guest.manage</c> (AM-03), not
    /// <c>guest.write</c>: a collaborator registers and corrects guests but does not delete them. A guest outside the caller's
    /// reach (SR-04b) answers 404.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = CasazenPolicies.GuestManage)]
    public async Task<IActionResult> Delete(Guid id)
    {
        logger.LogInformation("Deleting guest: {GuestId}", id);

        var scope = await ResolveScopeAsync();
        if (scope is null)
            return GuestNotFound();

        var result = await guestService.DeleteGuestAsync(scope, id, User.GetUserId(), HttpContext.RequestAborted);
        logger.LogInformation("Guest {GuestId} deletion completed: {Result}", id, result);
        return NoContent();
    }

    private Task<Guid?> ResolveOrgIdAsync() =>
        orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);

    /// <summary>
    /// What the caller reaches in its org (AM-03): the whole org, or the properties it was given. <c>null</c> when it has no org yet
    /// or no reach on it; that is never widened to the whole org, so the guest is then not found.
    /// </summary>
    private async Task<HostScope?> ResolveScopeAsync()
    {
        var orgId = await ResolveOrgIdAsync();
        return orgId is null
            ? null
            : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, HttpContext.RequestAborted);
    }

    /// <summary>The guest of the caller's org that the caller reaches (SR-04b); <c>null</c> for any other, answered as 404.</summary>
    private async Task<Guest?> FindGuestAsync(Guid guestId)
    {
        var scope = await ResolveScopeAsync();
        if (scope is null)
            return null;

        return await guestService.GetGuestAsync(scope, guestId, HttpContext.RequestAborted);
    }

    private ObjectResult GuestNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "guest_not_found", "GuestNotFound");
}
