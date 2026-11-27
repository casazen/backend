namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Why the dates of a manual block are closed (<see cref="CalendarBlockSource.Manual"/>, PC-09, A2-25): the host chooses
/// among the first three, <see cref="ModeChange"/> is written by CasaZen. Shown to the host only: the export to the OTAs
/// and the booking site say "taken", never why.
/// </summary>
public enum CalendarBlockReason
{
    Owner = 0,

    Maintenance = 1,

    Other = 2,

    ModeChange = 3,
}
