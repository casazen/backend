using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.OfficialData;

namespace Casazen.Infrastructure.OfficialData;

/// <summary>
/// Versioned prompt of the MEF tourist-tax extractor. Change <see cref="Version"/> whenever the text below changes:
/// the cache key includes it so a Hangfire retry of the same act does not pay twice after a prompt edit.
/// </summary>
public static class ComuneOfficialExtractPrompt
{
    public const string Version = "comune-mef-2026-10-v1";

    public const int MaxActChars = 40_000;

    public static string Build(
        Comune comune,
        string indexUrl,
        string pdfUrl,
        string actId,
        DateOnly? mefPublishedOn,
        string actText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sei un estrattore. Rispondi solo con un oggetto JSON, senza markdown.");
        sb.AppendLine();
        sb.AppendLine("ISTRUZIONI");
        sb.AppendLine("1. Usa esclusivamente il testo dell'atto fornito. Non usare conoscenza del modello, non stimare, non completare tabelle mancanti.");
        sb.AppendLine("2. Se un campo non è nel testo, metti null e readable = false su quel campo. Se nessuna tariffa locazioni brevi / CAV / locazione turistica è citata con importo, status = \"unreadable\".");
        sb.AppendLine("3. Ogni tariffa deve citare sourceUrl (URL esatto del PDF) e actId (tipo + numero + data, es. Giunta 220/2024-11-19 e, se presente, Consiglio 133/2024-12-19).");
        sb.AppendLine("4. Non includere nomi di persone, email, telefoni, codici fiscali, indirizzi di immobili, CIN di strutture.");
        sb.AppendLine("5. legalValue = false è costante. disclaimer: i dati sono reportistica CasaZen.");
        sb.AppendLine();
        sb.AppendLine("SCHEMA");
        sb.AppendLine("status: extracted | unreadable");
        sb.AppendLine("actId: string");
        sb.AppendLine("sourceUrl: string https");
        sb.AppendLine("mefPublishedOn: yyyy-MM-dd");
        sb.AppendLine("validFrom: yyyy-MM-dd");
        sb.AppendLine("rates[]: accommodationCategory, seasonStart, seasonEnd (MM-dd), calculationMethod (PerPersonPerNight | PercentOfNightlyPrice), ratePerPersonPerNight, percentOfNightlyPrice, capPerPersonPerNight, maxNights, minimumAge (0..18), reducedRateMaxAge, reducedRatePerPersonPerNight");
        sb.AppendLine("remittance: text, dueDayOfMonth, frequency (monthly | quarterly | other)");
        sb.AppendLine("legalValue: false");
        sb.AppendLine();
        sb.AppendLine("COMUNE (anagrafe pubblica ISTAT, non PII)");
        sb.AppendLine($"istatCode: {comune.IstatCode}");
        sb.AppendLine($"cadastralCode: {comune.CadastralCode}");
        sb.AppendLine($"name: {comune.Name}");
        sb.AppendLine($"provinceCode: {comune.ProvinceCode}");
        sb.AppendLine();
        sb.AppendLine("ATTO MEF");
        sb.AppendLine($"indexUrl: {indexUrl}");
        sb.AppendLine($"sourceUrl: {pdfUrl}");
        sb.AppendLine($"actId: {actId}");
        sb.AppendLine($"mefPublishedOn: {mefPublishedOn:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("TESTO ATTO");
        sb.AppendLine(Truncate(actText, MaxActChars));
        return sb.ToString();
    }

    public static string CacheKey(string istatCode, string actSha256) =>
        $"comune-official:{istatCode}:{actSha256}:{Version}";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
