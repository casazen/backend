using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>The account's sender preference, not its credentials or proof of operational activation.</summary>
[ApiController]
[Route("api/orgs/me/compliance-sender")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgComplianceSenderController(
    AppDbContext db,
    IOrgContextResolver orgContext,
    IAlloggiatiDelegationService delegation,
    TimeProvider clock,
    ILogger<OrgComplianceSenderController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<OrgComplianceSenderDto>> Get(CancellationToken ct)
    {
        var orgId = await orgContext.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound();
        var org = await db.Orgs.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null)
            return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await ReadAsync(org, ct));
    }

    [HttpPut]
    public async Task<ActionResult<OrgComplianceSenderDto>> Put(OrgComplianceSenderRequest request, CancellationToken ct)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        var orgId = await orgContext.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound();
        var org = await db.Orgs.SingleOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null)
            return NotFound();
        if (request.Sender is not (ComplianceSenderType.Manual or ComplianceSenderType.ChannelManager))
            return this.ApiProblem(422, "sender_casazen_not_available", "SenderCasaZenNotAvailable");
        if (request.ExpectedLastChangedAt != org.DefaultComplianceSenderChangedAt)
            return this.ApiProblem(409, "sender_changed_concurrently", "SenderChangedConcurrently");
        if (!request.Confirmed)
            return this.ApiProblem(409, "sender_switch_confirmation_required", "SenderSwitchConfirmationRequired");

        var consent = await delegation.GetAsync(org.Id, ct);
        var accepted = consent.Valid && consent.TextVersion == AlloggiatiDelegationTexts.CurrentVersion;
        if (request.Sender == ComplianceSenderType.ChannelManager && !accepted)
        {
            if (!request.DelegationConsent)
                return this.ApiProblem(403, "alloggiati_delegation_required", "AlloggiatiDelegationRequired");
            if (request.DelegationTextVersion != AlloggiatiDelegationTexts.CurrentVersion)
                return this.ApiProblem(409, "alloggiati_delegation_text_outdated", "AlloggiatiDelegationTextOutdated");
            await delegation.GrantAsync(org.Id, userId, AlloggiatiDelegationTexts.CurrentVersion, ct);
        }

        if (org.DefaultComplianceSender != request.Sender || request.AutoSendGuestsToOctorate is { } auto && auto != org.AutoSendGuestsToOctorate)
        {
            org.DefaultComplianceSender = request.Sender;
            org.DefaultComplianceSenderChangedAt = UtcDateTime.TruncateToMicroseconds(clock.GetUtcNow().UtcDateTime);
            org.DefaultComplianceSenderChangedByUserId = userId;
            if (request.AutoSendGuestsToOctorate is { } enabled && enabled != org.AutoSendGuestsToOctorate)
            {
                org.AutoSendGuestsToOctorate = enabled;
                org.AutoSendGuestsToOctorateChangedAt = org.DefaultComplianceSenderChangedAt;
                org.AutoSendGuestsToOctorateChangedByUserId = userId;
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return this.ApiProblem(409, "sender_changed_concurrently", "SenderChangedConcurrently");
            }
            logger.LogInformation("Org {OrgId}: compliance sender default changed to {Sender} by {UserId}", org.Id, request.Sender, userId);
        }
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await ReadAsync(org, ct));
    }

    private async Task<OrgComplianceSenderDto> ReadAsync(Casazen.Core.Entities.Org org, CancellationToken ct)
    {
        var consent = await delegation.GetAsync(org.Id, ct);
        var overrides = await db.PropertyComplianceSenders.AsNoTracking()
            .CountAsync(s => s.OrgId == org.Id && db.Properties.Any(p => p.Id == s.PropertyId && (p.Country == "IT" || p.Country == null || p.Country == "")), ct);
        return new(org.DefaultComplianceSender, org.DefaultComplianceSenderChangedAt,
            consent.Valid && consent.TextVersion == AlloggiatiDelegationTexts.CurrentVersion,
            AlloggiatiDelegationTexts.CurrentVersion, overrides, org.AutoSendGuestsToOctorate);
    }
}

public sealed record OrgComplianceSenderDto(ComplianceSenderType? Sender, DateTime? LastChangedAt, bool DelegationAccepted, string DelegationTextVersion, int PropertyOverrides, bool AutoSendGuestsToOctorate);

public sealed class OrgComplianceSenderRequest
{
    [Required]
    public ComplianceSenderType? Sender { get; set; }
    public DateTime? ExpectedLastChangedAt { get; set; }
    public bool Confirmed { get; set; }
    public bool DelegationConsent { get; set; }
    public string? DelegationTextVersion { get; set; }
    public bool? AutoSendGuestsToOctorate { get; set; }
}
