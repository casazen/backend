namespace Casazen.Core.Entities.Enums;

/// <summary>Who opened a dispute (SP-15c-c). Stored as an integer: append only.</summary>
public enum ServiceDisputeOpener
{
    /// <summary>The private customer of a supplier's showcase, who proved it knows the code and the e-mail address of the booking.</summary>
    Customer = 0,

    /// <summary>The host (the org that asked for the work).</summary>
    Host = 1,

    /// <summary>CasaZen itself: the supplier never marked the work completed and the time to do so passed (the no-show).</summary>
    System = 2,
}
