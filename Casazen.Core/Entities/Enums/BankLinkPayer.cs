namespace Casazen.Core.Entities.Enums;

/// <summary>Who linked the bank account a <see cref="Entities.BankLinkPayment"/> debits.</summary>
public enum BankLinkPayer
{
    /// <summary>The signed-in organisation (the landlord).</summary>
    Landlord = 0,

    /// <summary>The tenant of one lease, from the payment link of an installment.</summary>
    Tenant = 1,
}
