using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// The template of a lease contract is chosen by variant (TR). The first three are the ones of the canone libero and concordato, one
/// per <see cref="FiscalRegime"/> and with the same names, so the folders of <c>Casazen.Web/LeaseTemplates</c> and the variables of the
/// environment (<c>LeaseTemplates__Variants__{Variant}__VersionId</c>) are the ones they always were. <see cref="Transitorio"/> is the
/// model of the transitory contract, whatever the tax regime of the lease: the one type of contract the D.M. 16/01/2017 allows for it
/// (Allegato B, art. 2 c. 7), the same for the cedolare secca and for the ordinary regime.
/// </summary>
public enum LeaseTemplateVariant
{
    /// <summary>Canone libero (4+4) with the cedolare secca.</summary>
    CedolareSecca = 0,

    /// <summary>Canone libero (4+4) with the ordinary regime.</summary>
    RegimeOrdinario = 1,

    /// <summary>Canone concordato (3+2), whatever the tax regime.</summary>
    CanoneConcordato = 2,

    /// <summary>Transitory contract (1 to 18 months): the Allegato B of the D.M. 16/01/2017, whatever the tax regime.</summary>
    Transitorio = 3,
}

/// <summary>Which variant of the template a lease uses.</summary>
public static class LeaseTemplateVariants
{
    /// <summary>The variant that has the name of <paramref name="regime"/>.</summary>
    public static LeaseTemplateVariant Of(FiscalRegime regime) => regime switch
    {
        FiscalRegime.CedolareSecca => LeaseTemplateVariant.CedolareSecca,
        FiscalRegime.RegimeOrdinario => LeaseTemplateVariant.RegimeOrdinario,
        FiscalRegime.CanoneConcordato => LeaseTemplateVariant.CanoneConcordato,
        _ => throw new ArgumentOutOfRangeException(nameof(regime), regime, "Unknown fiscal regime."),
    };

    /// <summary>
    /// The variant of a lease of this <paramref name="type"/>: the transitory model for a transitory lease, whatever its regime (a 4+4 or a
    /// 3+2 model must not be used for it), otherwise the one of the legacy fiscal regime (<see cref="LeaseContractTerms.LegacyFiscalRegime"/>).
    /// </summary>
    public static LeaseTemplateVariant For(LeaseContractType type, FiscalRegime regime) =>
        type == LeaseContractType.Transitorio ? LeaseTemplateVariant.Transitorio : Of(regime);
}
