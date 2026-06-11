namespace Casazen.Core.Services;

public interface IBillingEntryGate
{
    Task AssertCanChargeAsync(CancellationToken cancellationToken = default);
}

public sealed class BillingGateClosedException : Exception
{
    public BillingGateClosedException() : base("Billing entry gate is closed") { }
}
