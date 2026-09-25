using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Admin CRUD of the canone concordato reference data (LT-13, A7-22): territorial agreements (status, expiry, rules
/// and bands) and the comune offices receiving the IMU communication. No deploy is needed to fix a PEC or a band;
/// every write is recorded in the audit log (<see cref="GetAudit"/>). Platform admins only (JWT role <c>Admin</c>);
/// this is global reference data, not tenant-owned, so there is no per-row ownership check.
/// </summary>
[ApiController]
[Route("api/admin/canone-concordato")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminCanoneConcordatoController(IRegulatoryReferenceDataAdminService admin) : ControllerBase
{
    public const string AgreementNotFoundCode = RegulatoryReferenceDataErrorCodes.AgreementNotFound;
    public const string ImuChannelNotFoundCode = RegulatoryReferenceDataErrorCodes.ImuChannelNotFound;

    [HttpGet("agreements")]
    public async Task<IActionResult> GetAgreements(CancellationToken cancellationToken) =>
        Ok(await admin.GetAgreementsAsync(cancellationToken));

    [HttpGet("agreements/{id:guid}")]
    public async Task<IActionResult> GetAgreement(Guid id, CancellationToken cancellationToken)
    {
        var agreement = await admin.GetAgreementAsync(id, cancellationToken);
        return agreement is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, AgreementNotFoundCode, "LtrAgreementNotFound")
            : Ok(agreement);
    }

    /// <summary>Status, source, expiry and every rule of the rent calculation. The verification date is never edited here.</summary>
    [HttpPut("agreements/{id:guid}")]
    public async Task<IActionResult> UpdateAgreement(
        Guid id, [FromBody] UpdateAgreementInput input, CancellationToken cancellationToken)
    {
        if (GetUserId() is not { } userId) return Unauthorized();
        var updated = await admin.UpdateAgreementAsync(id, input, userId, cancellationToken);
        return updated is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, AgreementNotFoundCode, "LtrAgreementNotFound")
            : Ok(updated);
    }

    [HttpPut("agreements/{agreementId:guid}/bands/{bandId:guid}")]
    public async Task<IActionResult> UpdateBand(
        Guid agreementId, Guid bandId, [FromBody] UpdateRentBandInput input, CancellationToken cancellationToken)
    {
        if (GetUserId() is not { } userId) return Unauthorized();
        var updated = await admin.UpdateBandAsync(agreementId, bandId, input, userId, cancellationToken);
        return updated is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, RegulatoryReferenceDataErrorCodes.BandNotFound, "LtrBandNotFound")
            : Ok(updated);
    }

    /// <summary>Records the date the agreement's data were checked against an official source, and the source.</summary>
    [HttpPost("agreements/{id:guid}/verify")]
    public async Task<IActionResult> VerifyAgreement(
        Guid id, [FromBody] MarkVerifiedRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId() is not { } userId) return Unauthorized();
        var updated = await admin.MarkAgreementVerifiedAsync(id, request.ToInput(), userId, cancellationToken);
        return updated is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, AgreementNotFoundCode, "LtrAgreementNotFound")
            : Ok(updated);
    }

    [HttpGet("imu-channels")]
    public async Task<IActionResult> GetImuChannels(CancellationToken cancellationToken) =>
        Ok(await admin.GetImuChannelsAsync(cancellationToken));

    [HttpPut("imu-channels/{id:guid}")]
    public async Task<IActionResult> UpdateImuChannel(
        Guid id, [FromBody] UpdateImuChannelInput input, CancellationToken cancellationToken)
    {
        if (GetUserId() is not { } userId) return Unauthorized();
        var updated = await admin.UpdateImuChannelAsync(id, input, userId, cancellationToken);
        return updated is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ImuChannelNotFoundCode, "LtrImuChannelNotFound")
            : Ok(updated);
    }

    [HttpPost("imu-channels/{id:guid}/verify")]
    public async Task<IActionResult> VerifyImuChannel(
        Guid id, [FromBody] MarkVerifiedRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId() is not { } userId) return Unauthorized();
        var updated = await admin.MarkImuChannelVerifiedAsync(id, request.ToInput(), userId, cancellationToken);
        return updated is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ImuChannelNotFoundCode, "LtrImuChannelNotFound")
            : Ok(updated);
    }

    /// <summary>Audit trail of one row (agreement or IMU channel), newest first.</summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit([FromQuery, Required] Guid entityId, CancellationToken cancellationToken) =>
        Ok(await admin.GetAuditAsync(entityId, cancellationToken));

    private string? GetUserId() => User.GetUserId();
}

/// <summary>Body of a "mark verified" request: <c>YYYY-MM-DD</c>, not in the future, and the source it was checked against.</summary>
public sealed class MarkVerifiedRequest
{
    [Required]
    public DateOnly? VerifiedAt { get; set; }

    [Required, MaxLength(500)]
    public string Source { get; set; } = string.Empty;

    public MarkVerifiedInput ToInput() => new(VerifiedAt!.Value, Source.Trim());
}
