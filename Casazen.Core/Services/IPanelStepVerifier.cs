using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What the verification of a guided panel step found (NF-107, ONB-04, D17, D31). <see cref="Verified"/>: CasaZen read the proof and the step is
/// done. <see cref="NotValid"/>: CasaZen read it and the step is not done, or not valid. <see cref="Unverifiable"/>: the panel offers no way to read
/// it (or the read was refused): only then may the person confirm by hand, so the confirmation stays the technical fallback and never the normal
/// path. The three are stored as the stable strings of <see cref="PanelVerificationOutcomes"/>.
/// </summary>
public enum PanelVerificationOutcome
{
    /// <summary>CasaZen read the proof: the step is done.</summary>
    Verified = 1,

    /// <summary>CasaZen read it and the step is not done or not valid: the person has to do it again.</summary>
    NotValid = 2,

    /// <summary>CasaZen cannot tell: the person confirms.</summary>
    Unverifiable = 3,
}

/// <summary>The stored string of a <see cref="PanelVerificationOutcome"/> (<see cref="PanelVerificationOutcomes"/>).</summary>
public static class PanelVerificationOutcomeExtensions
{
    public static string Code(this PanelVerificationOutcome outcome) => outcome switch
    {
        PanelVerificationOutcome.Verified => PanelVerificationOutcomes.Verified,
        PanelVerificationOutcome.NotValid => PanelVerificationOutcomes.NotValid,
        PanelVerificationOutcome.Unverifiable => PanelVerificationOutcomes.Unverifiable,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a verification outcome."),
    };
}

/// <summary>
/// The step a verifier is asked about: the org, the property (when the step has one), the kind and the channel of a channel access. It carries the
/// facts a verifier of another epic needs to find its own record (the marketplace epic reads the activation state of the channel), and nothing of
/// the person.
/// </summary>
/// <param name="OrgId">The org of the step: the verifier acts for it and for no other.</param>
/// <param name="PropertyId">The property of the step; <c>null</c> for a step of the whole org.</param>
/// <param name="Kind">The kind of the step.</param>
/// <param name="ChannelCode">The code of the channel of a <see cref="PanelStepKind.ChannelAccess"/> step; <c>null</c> otherwise.</param>
public sealed record PanelStepContext(Guid OrgId, Guid? PropertyId, PanelStepKind Kind, string? ChannelCode);

/// <summary>
/// The check of one kind of guided panel step (NF-107, E10/F01/US03/T01): reads, where CasaZen can, whether the step is done. There is one
/// default verifier for each <see cref="PanelStepKind"/> and an epic that can read more registers its own for the same kind with a higher
/// <see cref="Order"/> (the marketplace epic registers <c>ChannelAccessPanelVerifier</c>, E09/F02/US01/T02, which reads the activation state):
/// the verifier with the highest <see cref="Order"/> of a kind is the one that runs.
/// </summary>
/// <remarks>
/// <para><b>Failure.</b> A verifier answers <see cref="PanelVerificationOutcome.Unverifiable"/> for what only means «cannot tell» (no mapping, a
/// field the panel does not expose, a read Octorate refused). A failure worth a retry (Octorate down, the quota used) is thrown: the service counts
/// it as an attempt, decides nothing and never unlocks the confirmation with it.</para>
/// <para><b>Tenant.</b> A verifier acts only for <see cref="PanelStepContext.OrgId"/>.</para>
/// </remarks>
public interface IPanelStepVerifier
{
    /// <summary>The kind of step this verifier checks.</summary>
    PanelStepKind Kind { get; }

    /// <summary>The priority among the verifiers of the same kind: the highest wins. The default verifiers have 0.</summary>
    int Order { get; }

    /// <summary>
    /// True when the panel of this kind sends the person back to a CasaZen address (the registered callback): the step is then started in
    /// <see cref="PanelHandoffMode.RedirectCallback"/> and the callback with its opaque, single-use, expiring state completes the verification. False
    /// when the panel has no callback: <see cref="PanelHandoffMode.ManagedWindow"/>. No panel Octorate offers today sends the person back.
    /// </summary>
    bool SupportsCallback => false;

    /// <summary>Verifies the step.</summary>
    Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct);
}
