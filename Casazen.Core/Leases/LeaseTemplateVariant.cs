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
    CedolareSecca = 0,

    RegimeOrdinario = 1,

    CanoneConcordato = 2,

    Transitorio = 3,
}

public static class LeaseTemplateVariants
{
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
