using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The check of the guided step of the access to a channel, from the activation of the marketplace (E09/F02/US01/T02, NF-56, NF-107, S19): the
/// verifier of <see cref="PanelStepKind.ChannelAccess"/> with the higher <see cref="Order"/> than the default one, which always answers «cannot tell».
/// It takes the org, the property and the channel code of the step, finds the <see cref="Casazen.Core.Entities.MarketplaceActivation"/> of that org,
/// property and code, and answers <see cref="PanelVerificationOutcome.Verified"/> when <see cref="IMarketplaceChannelActivationService.GetStateAsync"/>
/// says the access on the panel is done.
/// </summary>
/// <remarks>
/// <para><b>Never «not valid».</b> Any other case is <see cref="PanelVerificationOutcome.Unverifiable"/>: no activation (the step is not one of the
/// marketplace's), a property that is no longer linked, a connector that is off, a connection that is not listed yet, an access not given yet. The
/// result on the panel may not be readable yet, and the person can then confirm it. So is a read the channel manager refused and a retry would not
/// change (the token, the accommodation unknown, a request it will not take, the other environment, the channel itself). Only a failure worth a later
/// retry (the channel manager down or silent, the quota used) is raised: the step counts it as an attempt and decides nothing.</para>
/// <para><b>Only reads.</b> It never starts, changes or completes an activation. The scope of the step was checked when the step was started and on
/// every read of it, so the state is read with the scope of the whole org the step belongs to, and for no other org.</para>
/// </remarks>
public sealed class ChannelAccessPanelVerifier(AppDbContext db, IMarketplaceChannelActivationService activations) : IPanelStepVerifier
{
    public PanelStepKind Kind => PanelStepKind.ChannelAccess;

    /// <summary>Higher than <c>ChannelAccessDefaultPanelVerifier</c> (0): the verifier with the highest order of a kind is the one that runs.</summary>
    public int Order => 10;

    public async Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PropertyId is not { } propertyId || context.ChannelCode?.Trim() is not { Length: > 0 } channelCode)
            return PanelVerificationOutcome.Unverifiable;

        // The activation of this org, property and channel (a channel or a tool: both are reached through the panel). Nothing of another org.
        var activationId = await db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == context.OrgId
                        && a.PropertyId == propertyId
                        && a.TargetCode == channelCode
                        && (a.Kind == MarketplaceActivationKind.Channel || a.Kind == MarketplaceActivationKind.Tool))
            .OrderBy(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (activationId is not { } id)
            return PanelVerificationOutcome.Unverifiable;

        try
        {
            var state = await activations.GetStateAsync(context.OrgId, new HostScope(context.OrgId), id, ct);
            return state.PanelAccessDone ? PanelVerificationOutcome.Verified : PanelVerificationOutcome.Unverifiable;
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException)
        {
            // The connector is off, the org is not connected, the property is no longer linked, the activation is gone, an operation of the D6
            // sequence is not verified for the channel: «cannot tell», not a verdict.
            return PanelVerificationOutcome.Unverifiable;
        }
        catch (OctorateException ex) when (ex is not (OctorateServerException or OctorateRateLimitException))
        {
            // The channel manager refused the read (the token, the accommodation unknown, a request it will not take, the other environment, the
            // channel itself): a retry will not change it, so the answer is «cannot tell» and the person may confirm. Only a failure worth a retry
            // (no answer, a 5xx, the quota used) is raised, as the contract of the verifiers says.
            return PanelVerificationOutcome.Unverifiable;
        }
    }
}
