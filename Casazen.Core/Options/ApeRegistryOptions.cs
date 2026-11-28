namespace Casazen.Core.Options;

public class ApeRegistryOptions
{
    public const string SectionName = "ApeRegistry";

    public bool Enabled { get; set; } = true;

    public string CenedBaseUrl { get; set; } = "https://www.dati.lombardia.it/";

    public string CenedDataset { get; set; } = "bbky-sde5";

    public int TimeoutSeconds { get; set; } = 10;
}
