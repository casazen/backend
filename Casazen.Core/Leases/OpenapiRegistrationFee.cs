using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Leases;

/// <summary>
/// Filing fee for an Openapi lease registration that is not canone concordato.
/// The percentage and the minimum are <see cref="RegistroTaxOptions"/>: the same registration-tax percentage the
/// canone concordato advisory already uses (there on 70% of the rent when the ATA relief applies, here on the full
/// annual rent, which is the ordinary branch of that same formula). No second rate is introduced.
/// Cedolare secca does not change the percentage: this is the fee CasaZen collects before the filing proceeds, not the
/// tax the Agenzia charges (that tax stays on the IBAN Openapi debits, and is zero under the cedolare).
/// </summary>
public static class OpenapiRegistrationFee
{
    /// <summary>Stripe metadata <c>kind</c>. Present so the payment is not read as a ConcordatoFacile payment (those have no kind).</summary>
    public const string StripeKind = "openapi-lease-registration";

    public const string UnpaidCode = "openapi_registration_unpaid";

    public const string AlreadyPaidCode = "openapi_registration_already_paid";

    /// <summary>This path does not file a canone concordato. That contract stays on its own flow.</summary>
    public const string NotThisContractCode = "openapi_registration_concordato";

    public static bool Applies(LeaseContractType contractType) => contractType != LeaseContractType.Concordato;

    public static OpenapiRegistrationQuote Quote(decimal monthlyRent, RegistroTaxOptions registro)
    {
        ArgumentNullException.ThrowIfNull(registro);
        var percent = registro.Rate;
        var annual = decimal.Round(monthlyRent * 12m, 2, MidpointRounding.AwayFromZero);
        var computed = decimal.Round(annual * percent, 2, MidpointRounding.AwayFromZero);
        var due = computed < registro.FirstYearMinimumEur ? registro.FirstYearMinimumEur : computed;
        if (due < 0)
            due = 0;
        var cents = (int)decimal.Round(due * 100m, 0, MidpointRounding.AwayFromZero);
        return new OpenapiRegistrationQuote(percent, cents);
    }
}

/// <param name="Percent">The rate in force when the quote was made (snapshot on the payment).</param>
/// <param name="AmountCents">What the host pays before the filing proceeds, in euro cents.</param>
public readonly record struct OpenapiRegistrationQuote(decimal Percent, int AmountCents);
