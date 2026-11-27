using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Leases;

public static class OpenapiRegistrationFee
{
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

public readonly record struct OpenapiRegistrationQuote(decimal Percent, int AmountCents);
