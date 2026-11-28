namespace Casazen.Core.Entities.Enums;

public enum InvoiceDataSource
{
    Octorate = 1,

    /// <summary>What the guest answered to «Ti serve la fattura?» in the public check-in.</summary>
    CheckIn = 2,

    Host = 3,
}

public static class InvoiceDataPrecedence
{
    public static bool Outranks(InvoiceDataSource stored, InvoiceDataSource incoming) => (int)stored > (int)incoming;
}
