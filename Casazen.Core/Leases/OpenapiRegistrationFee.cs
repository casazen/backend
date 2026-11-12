using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Leases;

/// <summary>
/// Customer price for an Openapi lease registration. It is deliberately independent of the registration tax: taxes due
/// to the Agenzia are collected by the provider through the IBAN supplied in the filing, while this checkout covers the
/// published Openapi service price and CasaZen's fixed service fee.
/// </summary>
public static class OpenapiRegistrationFee
{
    /// <summary>Stripe metadata <c>kind</c>. Present so the payment is not read as a ConcordatoFacile payment (those have no kind).</summary>
    public const string StripeKind = "openapi-lease-registration";

    public const string UnpaidCode = "openapi_registration_unpaid";

    public const string AlreadyPaidCode = "openapi_registration_already_paid";

    public const int OpenapiServiceCents = 1_290;
    public const int CasazenServiceCents = 2_000;

    public static bool Applies(LeaseContractType contractType) => true;

    public static OpenapiRegistrationQuote Quote(decimal monthlyRent, RegistroTaxOptions registro)
    {
        ArgumentNullException.ThrowIfNull(registro);
        return new OpenapiRegistrationQuote(0m, OpenapiServiceCents + CasazenServiceCents);
    }
}

/// <param name="Percent">The rate in force when the quote was made (snapshot on the payment).</param>
/// <param name="AmountCents">What the host pays before the filing proceeds, in euro cents.</param>
public readonly record struct OpenapiRegistrationQuote(decimal Percent, int AmountCents);
