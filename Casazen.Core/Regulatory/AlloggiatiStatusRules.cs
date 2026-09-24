using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// How an Alloggiati Web status reads for the host (CO-11, decision D6). CasaZen does not transmit yet (CO-13), so
/// from the arrival day on every communication not sent is "to send manually", whether or not the arrival-day job
/// has already run: a booking never looks done without a receipt or the host's declaration.
/// </summary>
public static class AlloggiatiStatusRules
{
    /// <summary>Sent with a receipt, or declared sent by the host.</summary>
    public static bool IsSent(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente;

    /// <summary>Error or rejection: the host must act.</summary>
    public static bool IsFailure(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Errore or AlloggiatiWebStatus.Rifiutato;

    /// <summary>
    /// Status shown for a booking: the stored one once past <see cref="AlloggiatiWebStatus.DaInviare"/>; otherwise
    /// (no report yet, or job not run yet) <see cref="AlloggiatiWebStatus.DaInviare"/> before the arrival day and
    /// <see cref="AlloggiatiWebStatus.DaInviareManualmente"/> from the arrival day in Europe/Rome.
    /// </summary>
    public static AlloggiatiWebStatus Effective(AlloggiatiWebStatus? stored, DateTime checkInDate, DateTime todayInRome)
    {
        if (stored is { } status && status != AlloggiatiWebStatus.DaInviare)
            return status;

        return AlloggiatiTerms.IsArrivalDayReached(checkInDate, todayInRome)
            ? AlloggiatiWebStatus.DaInviareManualmente
            : AlloggiatiWebStatus.DaInviare;
    }
}
