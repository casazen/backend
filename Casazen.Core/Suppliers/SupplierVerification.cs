using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The "Verificato" badge of a supplier (decision D11, SP-14) and the payments readiness it depends on. A supplier is
/// verified when its profile is <see cref="SupplierStatus.Active"/>, its Stripe Connect account can take charges and receive
/// payouts, and it has a VAT number (P.IVA). It is a read-only rule: it changes nothing of the activation or of the
/// payments, it only says what is true now. Single source for the supplier payments account state and, later, for the
/// showcase and the payment requests.
/// </summary>
public static class SupplierVerification
{
    /// <summary>The profile is not <see cref="SupplierStatus.Active"/> (still pending the activation wizard, or suspended).</summary>
    public const string ProfileNotActive = "profile_not_active";

    /// <summary>No Connect account yet, or Stripe has not enabled charges and payouts on it.</summary>
    public const string PaymentsNotEnabled = "payments_not_enabled";

    /// <summary>The profile has no VAT number.</summary>
    public const string VatNumberMissing = "vat_number_missing";

    /// <summary>
    /// The supplier can receive the payment of a job through CasaZen: a Connect account is linked <b>and</b> Stripe reports
    /// both charges and payouts enabled (<c>ConnectChargesEnabled</c> and <c>ConnectPayoutsEnabled</c>, cached from
    /// <c>account.updated</c>). Having started the onboarding is not enough.
    /// </summary>
    public static bool CanReceivePayments(string? connectedAccountId, bool chargesEnabled, bool payoutsEnabled) =>
        !string.IsNullOrWhiteSpace(connectedAccountId) && chargesEnabled && payoutsEnabled;

    /// <summary>
    /// What the supplier still lacks to be verified, in a stable order (<see cref="ProfileNotActive"/>,
    /// <see cref="PaymentsNotEnabled"/>, <see cref="VatNumberMissing"/>); empty when it is verified.
    /// </summary>
    public static IReadOnlyList<string> Missing(SupplierStatus status, bool canReceivePayments, string? vatNumber)
    {
        var missing = new List<string>(3);
        if (status != SupplierStatus.Active)
            missing.Add(ProfileNotActive);
        if (!canReceivePayments)
            missing.Add(PaymentsNotEnabled);
        if (string.IsNullOrWhiteSpace(vatNumber))
            missing.Add(VatNumberMissing);

        return missing;
    }

    /// <summary><see cref="Missing"/> is empty: Active profile, payments enabled on Stripe and a VAT number.</summary>
    public static bool IsVerified(SupplierStatus status, bool canReceivePayments, string? vatNumber) =>
        Missing(status, canReceivePayments, vatNumber).Count == 0;
}
