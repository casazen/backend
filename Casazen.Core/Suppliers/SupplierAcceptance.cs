namespace Casazen.Core.Suppliers;

/// <summary>How often a supplier says yes (SP-04e, SP-D13): one of the three numbers of the console home.</summary>
public static class SupplierAcceptance
{
    /// <summary>
    /// The acceptance rate: the requests the supplier <b>took</b> over the requests it <b>answered</b> (took plus refused), from 0 to 1
    /// with four decimals. <c>null</c> when it answered none: no answer is not "0 %", and the console shows a dash. A request
    /// cancelled after the take is still a request the supplier took, so it does not change the rate; one nobody answered in time
    /// (cancelled by CasaZen) is no answer either.
    /// </summary>
    public static double? Rate(int accepted, int rejected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(accepted);
        ArgumentOutOfRangeException.ThrowIfNegative(rejected);

        var answered = accepted + rejected;
        return answered == 0 ? null : Math.Round((double)accepted / answered, 4, MidpointRounding.AwayFromZero);
    }
}
