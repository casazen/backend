namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the VAT on CasaZen's commission is treated (<see cref="ServiceRequestPayment.FeeVatMode"/>). <b>[CONSULENTE FISCALE]</b>:
/// whether the commission percentage already includes VAT or VAT is added to it is open (decision D4), so nothing in SP-15a
/// sets this field or <see cref="ServiceRequestPayment.FeeVatCents"/>: they stay empty until the tax consultant decides. The
/// schema is ready so that the decision needs no migration of the payments. Stored as an integer: append only.
/// </summary>
public enum ServiceFeeVatMode
{
    /// <summary>The commission percentage is the amount with VAT inside.</summary>
    Included = 1,

    /// <summary>VAT is added on top of the commission.</summary>
    Added = 2,
}
