namespace Casazen.Core.Suppliers;

public static class SupplierAcceptance
{
    public static double? Rate(int accepted, int rejected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(accepted);
        ArgumentOutOfRangeException.ThrowIfNegative(rejected);

        var answered = accepted + rejected;
        return answered == 0 ? null : Math.Round((double)accepted / answered, 4, MidpointRounding.AwayFromZero);
    }
}
