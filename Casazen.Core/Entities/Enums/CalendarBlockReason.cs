namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Why the dates of a manual block are closed (<see cref="CalendarBlockSource.Manual"/>, PC-09, A2-25): the host chooses
/// among the first three, <see cref="ModeChange"/> is written by CasaZen. Shown to the host only: the export to the OTAs
/// and the booking site say "taken", never why.
/// </summary>
public enum CalendarBlockReason
{
    /// <summary>The owner (or someone the host lets in for free) stays there.</summary>
    Owner = 0,

    /// <summary>Works, repairs, cleaning out of the ordinary.</summary>
    Maintenance = 1,

    /// <summary>Any other reason; the host may describe it in the note.</summary>
    Other = 2,

    /// <summary>
    /// Held by CasaZen, not chosen by the host (PM-02): the property went from short stays to long-term leases and its
    /// dates are closed from the day of the change for about two years, so the portals that read the iCal export stop
    /// selling them. The host can neither create it (<c>calendar_block_invalid_reason</c>) nor remove it by hand
    /// (<c>calendar_block_held_by_mode_change</c>): it goes away when the property goes back to short stays. Stored as 3;
    /// append only, like every value of this enum.
    /// </summary>
    ModeChange = 3,
}
