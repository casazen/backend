namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Why the host closed the dates by hand (<see cref="CalendarBlockSource.Manual"/>, PC-09, A2-25). Shown to the host
/// only: the export to the OTAs and the booking site say "taken", never why.
/// </summary>
public enum CalendarBlockReason
{
    /// <summary>The owner (or someone the host lets in for free) stays there.</summary>
    Owner = 0,

    /// <summary>Works, repairs, cleaning out of the ordinary.</summary>
    Maintenance = 1,

    /// <summary>Any other reason; the host may describe it in the note.</summary>
    Other = 2,
}
