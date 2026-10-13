namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Type of a residential lease (LT-10, A7-13): it fixes the term rules and whether the rent is bound to a territorial
/// agreement. Separate from the tax regime (<see cref="LeaseTaxRegime"/>): a canone concordato lease can be under the
/// cedolare secca or the ordinary regime. Student leases (art. 5 c. 2 L. 431/1998) are not modelled.
/// </summary>
public enum LeaseContractType
{
    /// <summary>Canone libero, L. 431/1998 art. 2 c. 1 ("4+4"): at least 4 years.</summary>
    Libero = 0,

    /// <summary>Canone concordato, L. 431/1998 art. 2 c. 3 ("3+2"): at least 3 years, rent within the agreement's band.</summary>
    Concordato = 1,

    /// <summary>
    /// Transitorio, L. 431/1998 art. 5 c. 1 and D.M. 16/01/2017 art. 2: from 1 to 18 months. Only the term is checked:
    /// the transitory needs documented in the contract are not modelled (see <c>canone_concordato.md</c>).
    /// </summary>
    Transitorio = 2,
}
