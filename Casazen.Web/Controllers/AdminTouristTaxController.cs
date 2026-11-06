using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.TouristTax;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The reference data of the tourist tax per guest (NF-46, ADR-037-R3): the channel rules of the comuni and the Octorate code of the exemption
/// reasons. Platform data, the same for every org, written only by an administrator, each change stamped with the time and the administrator.
/// A channel rule never exists without its official source; no rule and no Octorate code is ever invented (Q-004).
/// </summary>
[ApiController]
[Route("api/admin/tourist-tax")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminTouristTaxController(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<AdminTouristTaxController> logger) : ControllerBase
{
    /// <summary>Problem code of a reason code that does not exist (404).</summary>
    public const string ReasonNotFoundCode = "tax_exemption_reason_not_found";

    /// <summary>Problem code of a source that is not an absolute https address (422).</summary>
    public const string SourceInvalidCode = "tax_rule_source_invalid";

    /// <summary>
    /// Adds or changes the rule of a channel in a comune (by channel and ISTAT code): who collects the tax there, from which official source
    /// (an absolute https address, required), whether it is active.
    /// </summary>
    [HttpPut("channel-rules")]
    [ProducesResponseType(typeof(TouristTaxChannelRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TouristTaxChannelRuleDto>> UpsertChannelRule(
        [FromBody] UpsertTouristTaxChannelRuleRequest request,
        CancellationToken cancellationToken)
    {
        var sourceUrl = request.SourceUrl?.Trim();
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SourceInvalidCode, "TouristTaxRuleSourceInvalid");

        var channel = request.Channel!.Value;
        var istatCode = request.IstatCode!.Trim();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rule = await db.TouristTaxChannelRules
            .FirstOrDefaultAsync(r => r.Channel == channel && r.IstatCode == istatCode, cancellationToken);
        if (rule is null)
        {
            rule = new TouristTaxChannelRule { Channel = channel, IstatCode = istatCode };
            db.TouristTaxChannelRules.Add(rule);
        }

        rule.Collector = request.Collector!.Value;
        rule.SourceUrl = sourceUrl!;
        rule.IsActive = request.IsActive;
        rule.VerifiedAt = request.VerifiedAt ?? now;
        rule.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        rule.UpdatedAt = now;
        rule.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Tourist tax channel rule {Channel}/{IstatCode} set by admin {UserId}: {Collector}, active {Active}",
            channel, istatCode, User.GetUserId(), rule.Collector, rule.IsActive);
        return Ok(TouristTaxChannelRuleDto.From(rule));
    }

    /// <summary>Sets the Octorate code of an exemption reason (null: no known match) and whether it can be chosen.</summary>
    [HttpPut("exemption-reasons/{code}")]
    [ProducesResponseType(typeof(AdminTouristTaxExemptionReasonDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminTouristTaxExemptionReasonDto>> UpdateExemptionReason(
        string code,
        [FromBody] UpdateTouristTaxExemptionReasonRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var reason = await db.TouristTaxExemptionReasons.FirstOrDefaultAsync(r => r.Code == normalized, cancellationToken);
        if (reason is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ReasonNotFoundCode, "TouristTaxExemptionReasonNotFound");

        reason.OctorateExemptionCode = string.IsNullOrWhiteSpace(request.OctorateExemptionCode)
            ? null
            : request.OctorateExemptionCode.Trim();
        reason.IsActive = request.IsActive;
        reason.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        reason.UpdatedByUserId = User.GetUserId();
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Tourist tax exemption reason {Code} set by admin {UserId}: Octorate code {OctorateCode}, active {Active}",
            reason.Code, User.GetUserId(), reason.OctorateExemptionCode, reason.IsActive);
        return Ok(AdminTouristTaxExemptionReasonDto.From(reason));
    }
}
