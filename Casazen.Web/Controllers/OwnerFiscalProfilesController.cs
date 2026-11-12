using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The fiscal and accounting policy of the owners of an agency (NF-109, NF-110, ADR-012, E06/F05/US02/T01): the rule sets the qualified accountant approves
/// and the effective-dated fiscal profile of each owner. Reading needs <c>agency.owner.view</c> and answers the identifiers, the address and the bank
/// account <b>masked</b>; only <c>agency.owner.financial.manage</c> reads them in full (audit-logged with ids, never the values) and changes profiles and
/// rule sets. An owner of another org is a 404.
/// </summary>
[ApiController]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class OwnerFiscalProfilesController(
    IOwnerFiscalProfileService profiles,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService,
    ILogger<OwnerFiscalProfilesController> logger) : ControllerBase
{
    /// <summary>The profiles of an owner, the newest first; full values only with <c>agency.owner.financial.manage</c>.</summary>
    [HttpGet("api/owners/{ownerId:guid}/fiscal-profiles")]
    [ProducesResponseType(typeof(List<OwnerFiscalProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<OwnerFiscalProfileDto>>> List(Guid ownerId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var full = await CanSeeFullValuesAsync();
        var list = await profiles.ListProfilesAsync(orgId.Value, ownerId, HttpContext.RequestAborted);
        if (full)
        {
            // Audit: the caller read the full fiscal identifiers of the owner (ADR-012-R3). Never the values.
            logger.LogInformation("Full fiscal profiles of owner {OwnerId} read by user {UserId}", ownerId, User.FindFirst("sub")?.Value);
        }

        return Ok(list.Select(p => OwnerFiscalProfileDto.From(p, full)).ToList());
    }

    /// <summary>
    /// A new profile of the owner from <c>validFrom</c> (the one in force ends the day before). 422 <c>owner_fiscal_profile_invalid</c> (with the
    /// <c>field</c>); 409 <c>owner_fiscal_profile_overlap</c>.
    /// </summary>
    [HttpPost("api/owners/{ownerId:guid}/fiscal-profiles")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerFiscalProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerFiscalProfileDto>> Save(Guid ownerId, [FromBody] OwnerFiscalProfileCommand command)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var profile = await profiles.SaveProfileAsync(orgId.Value, ownerId, command, User.FindFirst("sub")?.Value, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(List), new { ownerId }, OwnerFiscalProfileDto.From(profile, full: true));
    }

    /// <summary>
    /// Whether the owner can be used on <c>date</c> (a statement, a payout): <c>{ ready: true, profileId, policyId, ruleCode, ruleVersion, jurisdiction }</c>,
    /// or the 422 that blocks it (<c>owner_fiscal_profile_missing</c>, <c>owner_fiscal_profile_incomplete</c>, <c>owner_fiscal_policy_missing</c>,
    /// <c>owner_fiscal_policy_not_approved</c>, <c>owner_fiscal_policy_not_effective</c>, <c>owner_fiscal_policy_jurisdiction_mismatch</c>).
    /// </summary>
    [HttpGet("api/owners/{ownerId:guid}/fiscal-profiles/readiness")]
    [ProducesResponseType(typeof(OwnerFiscalReadinessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerFiscalReadinessDto>> Readiness(Guid ownerId, [FromQuery] DateOnly date)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var resolution = await profiles.ResolveAsync(orgId.Value, ownerId, date, HttpContext.RequestAborted);
        return Ok(new OwnerFiscalReadinessDto(true, resolution.Profile.Id, resolution.Policy.Id, resolution.Policy.RuleCode, resolution.Policy.Version, resolution.Policy.Jurisdiction));
    }

    /// <summary>The rule sets of the agency.</summary>
    [HttpGet("api/owner-accounting/policies")]
    [ProducesResponseType(typeof(List<OwnerAccountingPolicyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnerAccountingPolicyDto>>> Policies()
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        return Ok((await profiles.ListPoliciesAsync(orgId.Value, HttpContext.RequestAborted)).Select(OwnerAccountingPolicyDto.From).ToList());
    }

    /// <summary>A new rule set, not approved. 422 <c>owner_policy_invalid</c> (with the <c>field</c>); 409 <c>owner_policy_version_exists</c>.</summary>
    [HttpPost("api/owner-accounting/policies")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerAccountingPolicyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerAccountingPolicyDto>> CreatePolicy([FromBody] OwnerAccountingPolicyCommand command)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var policy = await profiles.CreatePolicyAsync(orgId.Value, command, User.FindFirst("sub")?.Value, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(Policies), new { }, OwnerAccountingPolicyDto.From(policy));
    }

    /// <summary>Records the qualified accountant's approval (<c>{ approvedByName }</c>); an approved rule set never changes (409).</summary>
    [HttpPost("api/owner-accounting/policies/{policyId:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerAccountingPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerAccountingPolicyDto>> ApprovePolicy(Guid policyId, [FromBody] ApproveOwnerPolicyRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var policy = await profiles.ApprovePolicyAsync(orgId.Value, policyId, request.ApprovedByName ?? string.Empty, User.FindFirst("sub")?.Value, HttpContext.RequestAborted);
        return Ok(OwnerAccountingPolicyDto.From(policy));
    }

    private async Task<bool> CanSeeFullValuesAsync() =>
        (await authorizationService.AuthorizeAsync(User, CasazenPolicies.AgencyOwnerFinancialManage)).Succeeded;
}

/// <summary>A fiscal profile as the API gives it: the identifiers, the address and the bank account masked unless the caller manages the owners' finances.</summary>
public sealed record OwnerFiscalProfileDto(
    Guid Id,
    Guid PropertyOwnerId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string EntityKind,
    string? LegalName,
    string? TaxResidenceCountry,
    string? LegalAddress,
    string? FiscalCode,
    string? VatNumber,
    string? ForeignTaxId,
    string? SettlementCurrency,
    string? BankAccountReference,
    string? MandateRole,
    DateOnly? MandateFrom,
    DateOnly? MandateTo,
    string? RevenueRecognitionRule,
    string? ExpenseCommissionModel,
    string? WorkingCapitalRule,
    string? TaxJurisdiction,
    Guid? AccountingPolicyId,
    bool Masked)
{
    public static OwnerFiscalProfileDto From(OwnerFiscalProfile p, bool full) => new(
        p.Id,
        p.PropertyOwnerId,
        p.ValidFrom,
        p.ValidTo,
        p.EntityKind.ToString(),
        full ? p.LegalName : OwnerFiscalMasking.Mask(p.LegalName),
        p.TaxResidenceCountry,
        full ? p.LegalAddress : OwnerFiscalMasking.Mask(p.LegalAddress),
        full ? p.FiscalCode : OwnerFiscalMasking.Mask(p.FiscalCode),
        full ? p.VatNumber : OwnerFiscalMasking.Mask(p.VatNumber),
        full ? p.ForeignTaxId : OwnerFiscalMasking.Mask(p.ForeignTaxId),
        p.SettlementCurrency,
        full ? p.BankAccountReference : OwnerFiscalMasking.Mask(p.BankAccountReference),
        p.MandateRole,
        p.MandateFrom,
        p.MandateTo,
        p.RevenueRecognitionRule,
        p.ExpenseCommissionModel,
        p.WorkingCapitalRule,
        p.TaxJurisdiction,
        p.AccountingPolicyId,
        !full);
}

/// <summary>A rule set: its identity, validity, source, parameters and approval.</summary>
public sealed record OwnerAccountingPolicyDto(
    Guid Id,
    string Jurisdiction,
    string RuleCode,
    string Version,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string SourceUrl,
    string SourceSha256,
    OwnerAccountingRuleParameters? Parameters,
    bool Approved,
    DateTime? ApprovedAt,
    string? ApprovedByName)
{
    public static OwnerAccountingPolicyDto From(OwnerAccountingPolicy p) => new(
        p.Id, p.Jurisdiction, p.RuleCode, p.Version, p.ValidFrom, p.ValidTo, p.SourceUrl, p.SourceSha256,
        OwnerAccountingRuleParameters.FromJson(p.ParametersJson), p.ApprovedAt is not null, p.ApprovedAt, p.ApprovedByName);
}

/// <summary>The readiness of an owner on a day: the profile and the approved rule that apply.</summary>
public sealed record OwnerFiscalReadinessDto(bool Ready, Guid ProfileId, Guid PolicyId, string RuleCode, string RuleVersion, string Jurisdiction);

/// <summary>The approval of a rule set: the qualified accountant who approved it.</summary>
public sealed record ApproveOwnerPolicyRequest
{
    public string? ApprovedByName { get; init; }
}
