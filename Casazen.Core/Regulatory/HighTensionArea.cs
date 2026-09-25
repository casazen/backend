using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Whether a comune is "ad alta tensione abitativa" (ATA) for the canone concordato reliefs, as far as CasaZen knows
/// (<see cref="HighTensionAreaComune"/>). The list is incomplete: a comune missing from it may still be ATA.
/// </summary>
public enum HighTensionAreaStatus
{
    /// <summary>The comune is not in CasaZen's ATA list: its status is unknown.</summary>
    NotListed = 0,

    /// <summary>The comune is an ATA candidate whose listing was not checked on the official text.</summary>
    Unverified = 1,

    /// <summary>The ATA listing was checked on the official text (<see cref="HighTensionAreaComune.VerifiedDirectly"/>).</summary>
    Verified = 2,
}

/// <summary>
/// Single rule for the ATA reliefs of a canone concordato lease (LT-08), shared by the canone concordato calculator and
/// the lease tax advisory so that the two never disagree (A7-09).
/// </summary>
/// <remarks>
/// <c>.claude/context/regulations/fiscale.md</c> L8 and L11 (Agenzia delle Entrate, class U): the cedolare secca at 10%
/// and the registration tax on 70% of the rent apply to canone concordato leases in ATA comuni. CasaZen applies them only
/// when the ATA listing is <see cref="HighTensionAreaStatus.Verified"/>; an unverified or missing listing gets the
/// ordinary values (21%, full base), with a note.
/// </remarks>
public static class HighTensionArea
{
    public static HighTensionAreaStatus StatusOf(HighTensionAreaComune? comune) => comune switch
    {
        null => HighTensionAreaStatus.NotListed,
        { VerifiedDirectly: true } => HighTensionAreaStatus.Verified,
        _ => HighTensionAreaStatus.Unverified,
    };

    /// <summary>True only for a verified ATA comune.</summary>
    public static bool ReliefsApply(HighTensionAreaStatus status) => status == HighTensionAreaStatus.Verified;

    /// <summary>True only for a verified ATA comune.</summary>
    public static bool ReliefsApply(HighTensionAreaComune? comune) => ReliefsApply(StatusOf(comune));
}
