namespace Casazen.Core.Options;

public class RliOptions
{
    public const string SectionName = "Rli";

    public string TosVersion { get; set; } = "2026-08-rli-delega-bozza";

    public string AttestationText { get; set; } =
        "Dichiaro di essere il locatore o un intermediario da me autorizzato e che la responsabilita del deposito RLI resta a me / al mio intermediario abilitato. CasaZen agisce solo come software di supporto. Testo bozza da confermare con legale.";

    public bool FilingEnabled { get; set; }
}

public class CedolareAdvisoryOptions
{
    public const string SectionName = "CedolareAdvisory";

    public decimal CedolareSeccaRate { get; set; } = 0.21m;
    public decimal CanoneConcordatoRate { get; set; } = 0.10m;
    public decimal RegistroRate { get; set; } = 0.02m;
    public decimal BolloEur { get; set; } = 16.00m;

    public string Disclaimer { get; set; } =
        "Informativa, non consulenza fiscale. Bozza da confermare con un legale o un intermediario abilitato. CasaZen non e' un intermediario abilitato ai sensi del DPR 322/1998.";

    public string OrdinaryIrpefNote { get; set; } =
        "IRPEF a scaglioni: non calcolata da CasaZen (informativa, non consulenza fiscale).";
}

public class LeaseTemplateOptions
{
    public const string SectionName = "LeaseTemplates";

    public Dictionary<string, LeaseTemplateVariantOptions> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class LeaseTemplateVariantOptions
{
    public string VersionId { get; set; } = "dev-stub";
    public bool Approved { get; set; }
}
