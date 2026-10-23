namespace Casazen.Core.Options;

/// <summary>
/// Section <c>ApeRegistry</c>: the public register asked for the code of an APE that cannot be read on the file
/// (<see cref="Casazen.Core.Services.IApeRegistryLookup"/>). Today the CENED+2 open data of Regione Lombardia on
/// dati.lombardia.it (Socrata, no key). Runbook <c>docs/runbooks/documenti-multipagina.md</c>.
/// </summary>
public class ApeRegistryOptions
{
    public const string SectionName = "ApeRegistry";

    /// <summary>When false nobody is asked: the code stays to be read on the file or typed. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The open data site of Regione Lombardia.</summary>
    public string CenedBaseUrl { get; set; } = "https://www.dati.lombardia.it/";

    /// <summary>The dataset "Database CENED+2 - Certificazione ENergetica degli EDifici" (APE from DGR 3868/2015).</summary>
    public string CenedDataset { get; set; } = "bbky-sde5";

    public int TimeoutSeconds { get; set; } = 10;
}
