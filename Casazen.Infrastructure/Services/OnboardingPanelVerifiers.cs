using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The default check of the guided step of the police connection (NF-107, E10/F01/US03/T01, D17, D31): it asks Octorate for the
/// <c>policeAccount</c> of the accommodation the property is confirmed to (<see cref="IPropertyMappingReader"/>, never the mapping tables) and
/// reads <c>valid</c>. <c>valid</c> true is <see cref="PanelVerificationOutcome.Verified"/>, false is <see cref="PanelVerificationOutcome.NotValid"/>;
/// anything else is <see cref="PanelVerificationOutcome.Unverifiable"/>: the property has no confirmed listing (or the step names no property), the
/// mapping is not provided, the account is absent (an accommodation created through the API has none: S25) or the read was refused. A read that
/// failed and is worth a retry (Octorate down, the quota used) is thrown and decides nothing.
/// </summary>
/// <remarks>The code of the Alloggiati account and the web service key the same object holds are never read: the client reads three members.</remarks>
public sealed class PoliceConnectionPanelVerifier(
    IPropertyMappingReader mappings,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope) : IPanelStepVerifier
{
    public PanelStepKind Kind => PanelStepKind.PoliceConnection;

    public int Order => 0;

    public async Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PropertyId is not { } propertyId || !mappings.IsProvided)
            return PanelVerificationOutcome.Unverifiable;

        var accommodations = await mappings.GetConfirmedAccommodationIdsAsync(context.OrgId, [propertyId], ct);
        if (!accommodations.TryGetValue(propertyId, out var accommodationId) || string.IsNullOrWhiteSpace(accommodationId))
            return PanelVerificationOutcome.Unverifiable;

        OctoratePoliceAccountStatus status;
        using (orgScope.Use(context.OrgId))
            status = await client.GetPoliceAccountStatusAsync(accommodationId, ct);

        return status switch
        {
            { Readable: true, Valid: true } => PanelVerificationOutcome.Verified,
            { Readable: true, Valid: false } => PanelVerificationOutcome.NotValid,
            _ => PanelVerificationOutcome.Unverifiable,
        };
    }
}

/// <summary>
/// The default check of the guided step of the ISTAT connection: Octorate exposes nothing CasaZen can read, and the result of the connection arrives
/// by e-mail (D15), so it is always <see cref="PanelVerificationOutcome.Unverifiable"/> and the person confirms. An epic that can read more
/// registers its own verifier for the kind with a higher <see cref="IPanelStepVerifier.Order"/>.
/// </summary>
public sealed class IstatConnectionPanelVerifier : IPanelStepVerifier
{
    public PanelStepKind Kind => PanelStepKind.IstatConnection;

    public int Order => 0;

    public Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct) =>
        Task.FromResult(PanelVerificationOutcome.Unverifiable);
}

/// <summary>
/// The default check of the guided step of the access to a channel: the panel offers no callback and nothing here reads the access, so it is always
/// <see cref="PanelVerificationOutcome.Unverifiable"/> until the marketplace epic registers its own verifier for the kind with a higher
/// <see cref="IPanelStepVerifier.Order"/> (<c>ChannelAccessPanelVerifier</c>, E09/F02/US01/T02, which reads the activation state).
/// </summary>
public sealed class ChannelAccessDefaultPanelVerifier : IPanelStepVerifier
{
    public PanelStepKind Kind => PanelStepKind.ChannelAccess;

    public int Order => 0;

    public Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct) =>
        Task.FromResult(PanelVerificationOutcome.Unverifiable);
}
