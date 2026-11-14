using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The support vault as CasaZen support fills it (E20/F03/US01/T01, decision O5, PO-19): the user of the channel manager panel of a structure CasaZen
/// created arrives by e-mail at the CasaZen address of the org only; support copies it here, encrypted, and deletes the e-mail
/// (<c>docs/runbooks/onboarding-assist.md</c>). This controller stores, lists and removes credentials: it never answers a value. The values are
/// opened only on an open «Do it for me» request (<see cref="AdminOnboardingAssistController.Reveal"/>).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AdminOnly"/>, the platform administrators; no host policy opens these routes.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>, like the «Do it for me» requests: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/admin/onboarding/support-vault")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
[PrivateAnswer]
public sealed class AdminSupportVaultController(ISupportVaultService vault) : ControllerBase
{
    /// <summary>What the vault holds for an org: kind, property, dates and who stored it last, never a value.</summary>
    /// <response code="200">The credentials of the org (none: an empty list).</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<SupportVaultSecretDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<SupportVaultSecretDto>>> List([FromQuery] Guid orgId, CancellationToken cancellationToken) =>
        Ok((await vault.ListAsync(orgId, cancellationToken)).Select(SupportVaultSecretDto.From).ToList());

    /// <summary>Stores, or replaces, a credential of an org (a property of it, or the whole org). Answers its presence, never the values.</summary>
    /// <response code="200">The credential as anybody reads it.</response>
    /// <response code="404"><c>support_vault_org_not_found</c>, <c>property_not_found</c>.</response>
    /// <response code="422"><c>support_vault_kind_invalid</c>, <c>support_vault_value_invalid</c>.</response>
    [HttpPut]
    [ProducesResponseType(typeof(SupportVaultSecretDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupportVaultSecretDto>> Store([FromBody] StoreSupportVaultSecretDto? body, CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (body?.OrgId is not { } orgId || body.Kind is not { } kind)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SupportVaultCodes.KindInvalid, "SupportVaultKindInvalid");

        var input = new SupportVaultSecretInput(orgId, body.PropertyId, kind, body.Username ?? string.Empty, body.Password ?? string.Empty, body.ExtraCode);
        return Ok(SupportVaultSecretDto.From(await vault.StoreAsync(staffUserId, input, cancellationToken)));
    }

    /// <summary>Removes a credential of the vault (204, also when there was none).</summary>
    [HttpDelete("{secretId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid secretId, CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        await vault.DeleteAsync(staffUserId, secretId, cancellationToken);
        return NoContent();
    }
}
