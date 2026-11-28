using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Infrastructure.Identity;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The evidence kept for the person who reviews a self-hosted identity verification (E73/F01/US02/T02, NF-195, decision D9): the list of the pictures of a booking and one picture,
/// decrypted. Under the policy of the class (<c>verification.decision</c>: owner, administrators and property managers, on the properties they reach) and behind
/// <see cref="FeatureFlags.CheckInIdentityReviewEvidence"/> with the two flags of the self-hosted verification (404 while one is off). A booking of another organization or of a
/// property the caller does not reach, and an evidence that is deleted, expired or not of that booking, are the same 404 <c>identity_evidence_not_found</c>: nothing says that it exists.
/// Every picture served writes first a line <c>IdentityEvidenceViewed</c> in the activity log of the organization (the service writes it, never this controller); the response is never
/// cached (<c>Cache-Control: no-store, private</c>, <c>Pragma: no-cache</c>), never sniffed and shown inline. The bytes in clear are zeroed once written.
/// </summary>
public partial class VerificationController
{
    [HttpGet("{bookingId:guid}/evidence")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [FeatureGate(FeatureFlags.CheckInIdentityReviewEvidence)]
    [ProducesResponseType(typeof(IReadOnlyList<IdentityEvidenceItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<IdentityEvidenceItemDto>>> GetEvidence(
        Guid bookingId,
        [FromServices] IdentityReviewEvidenceStore evidence,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        var items = await evidence.ListAsync(scope, bookingId, cancellationToken);
        if (items is null)
            return EvidenceNotFound();

        NeverCached();
        return Ok(items.Select(IdentityEvidenceItemDto.From).ToList());
    }

    [HttpGet("{bookingId:guid}/evidence/{evidenceId:guid}")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [FeatureGate(FeatureFlags.CheckInIdentityReviewEvidence)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEvidenceContent(
        Guid bookingId,
        Guid evidenceId,
        [FromServices] IdentityReviewEvidenceStore evidence,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        var content = await evidence.ReadAsync(scope, bookingId, evidenceId, userId, cancellationToken);
        if (content is null)
            return EvidenceNotFound();

        try
        {
            NeverCached();
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.ContentDisposition = "inline";
            Response.ContentType = content.ContentType;
            Response.ContentLength = content.Bytes.Length;
            await Response.Body.WriteAsync(content.Bytes, cancellationToken);
            return new EmptyResult();
        }
        finally
        {
            Array.Clear(content.Bytes);
        }
    }

    private ObjectResult EvidenceNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, IdentityApiErrors.IdentityEvidenceNotFound, "IdentityEvidenceNotFound");

    private void NeverCached()
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
    }
}
