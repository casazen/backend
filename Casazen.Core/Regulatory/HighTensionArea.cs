using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

public enum HighTensionAreaStatus
{
    NotListed = 0,

    Unverified = 1,

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

    public static bool ReliefsApply(HighTensionAreaStatus status) => status == HighTensionAreaStatus.Verified;

    public static bool ReliefsApply(HighTensionAreaComune? comune) => ReliefsApply(StatusOf(comune));
}
