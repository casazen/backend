using Casazen.Core.Entities;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>OfficialReferenceData</c>: official public URLs of the ISTAT comuni list, the Alloggiati Web code
/// tables and the tourist-tax pages of the pilot comuni. No paid API. Runbook <c>docs/runbooks/comuni-istat.md</c>.
/// </summary>
public class OfficialReferenceDataOptions
{
    public const string SectionName = "OfficialReferenceData";

    public const string DefaultIstatCatalogUrl =
        "https://www.istat.it/classificazione/codici-dei-comuni-delle-province-e-delle-regioni/";

    public const string DefaultIstatComuniCsvUrl =
        "https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv";

    public const string DefaultAlloggiatiTabellePageUrl =
        "https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Tabelle.aspx";

    public const string DefaultAlloggiatiDownloadBaseUrl =
        "https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/ashx/Download.ashx";

    /// <summary>
    /// MEF page of the imposta di soggiorno (PO source). The daily CSV permalink of the <c>nuova_at</c> archive
    /// is discovered from this page, the same way the ISTAT catalog page yields the comuni file.
    /// </summary>
    public const string DefaultMefNuovaAtCatalogUrl =
        "https://www.finanze.gov.it/it/fiscalita/fiscalita-regionale-e-locale/TARI-Imposta-di-soggiorno-e-altri-tributi-comunali/imposta-di-soggiorno/index.html";

    /// <summary>
    /// Fallback CSV URL when the catalog page does not expose a <c>nuova_at</c> permalink. Never a blog URL.
    /// </summary>
    public const string DefaultMefNuovaAtCsvUrl =
        "https://www1.finanze.gov.it/finanze/dipartimentopolitichefiscali/fiscalitalocale/nuova_at/file/ElencoAtti.csv";

    /// <summary>When false the Hangfire job is a no-op (tests). On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Load the embedded Alloggiati tables at startup when the database has none. On by default.</summary>
    public bool SeedAlloggiatiOnStartup { get; set; } = true;

    public string IstatCatalogUrl { get; set; } = DefaultIstatCatalogUrl;

    public string IstatComuniCsvUrl { get; set; } = DefaultIstatComuniCsvUrl;

    public string AlloggiatiTabellePageUrl { get; set; } = DefaultAlloggiatiTabellePageUrl;

    public string AlloggiatiDownloadBaseUrl { get; set; } = DefaultAlloggiatiDownloadBaseUrl;

    public string MefNuovaAtCatalogUrl { get; set; } = DefaultMefNuovaAtCatalogUrl;

    public string MefNuovaAtCsvUrl { get; set; } = DefaultMefNuovaAtCsvUrl;

    /// <summary>Reuse a current profile for this many days unless the MEF act hash changed.</summary>
    public int ComuneProfileReuseDays { get; set; } = 30;

    /// <summary>
    /// Institutional tourist-tax pages of the pilot comuni already in the product. Never a national invented list.
    /// A row without a HTTPS URL of an allowed host is ignored.
    /// </summary>
    public List<OfficialTouristTaxSourceOptions> TouristTaxSources { get; set; } = [];

    public string AlloggiatiDownloadUrl(AlloggiatiCodeTable table)
    {
        var id = table switch
        {
            AlloggiatiCodeTable.Comuni => "0&N=COMUNI",
            AlloggiatiCodeTable.Stati => "1&N=STATI",
            AlloggiatiCodeTable.Documenti => "2&N=DOCUMENTI",
            AlloggiatiCodeTable.TipiAlloggiato => "3&N=TIPO_ALLOGGIATO",
            _ => null,
        };
        return id is null ? AlloggiatiDownloadBaseUrl : $"{AlloggiatiDownloadBaseUrl}?ID={id}";
    }
}

/// <summary>One comune whose tourist-tax page the job re-reads. Amounts are written only when extracted deterministically.</summary>
public class OfficialTouristTaxSourceOptions
{
    public string IstatCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Authority { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;
}
