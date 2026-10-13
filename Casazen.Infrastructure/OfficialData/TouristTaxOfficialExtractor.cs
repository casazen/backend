using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using UglyToad.PdfPig;

namespace Casazen.Infrastructure.OfficialData;

/// <summary>
/// Deterministic tourist-tax amounts from an institutional HTML page or a textual PDF of a pilot comune (RS-7).
/// A scan, an empty file or a page without a locazioni-brevi tariff is a failure: the caller must not invent an amount.
/// </summary>
public static partial class TouristTaxOfficialExtractor
{
    public static TouristTaxExtraction TryExtract(string istatCode, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return TouristTaxExtraction.Failed("empty_document");

        var text = ReadDocumentText(bytes);
        if (string.IsNullOrWhiteSpace(text))
            return TouristTaxExtraction.Failed("no_extractable_text");

        return TryExtractFromText(istatCode, text);
    }

    public static TouristTaxExtraction TryExtractFromText(string istatCode, string text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return TouristTaxExtraction.Failed("no_extractable_text");

        return istatCode switch
        {
            "015146" => Milano(normalized),
            "058091" => Roma(normalized),
            "013075" => Como(normalized),
            "048017" => Firenze(normalized),
            "063049" => Napoli(normalized),
            "001272" => Torino(normalized),
            "027042" => Venezia(normalized),
            "037006" => Bologna(normalized),
            "108040" or "108019" => TouristTaxExtraction.Failed("no_tourist_tax_tariff_in_document"),
            _ => TouristTaxExtraction.Failed("no_extractor_for_comune"),
        };
    }

    internal static string ReadDocumentText(ReadOnlySpan<byte> bytes)
    {
        if (IsPdf(bytes))
        {
            using var document = PdfDocument.Open(bytes.ToArray());
            return string.Join(' ', document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        }

        var utf8 = Encoding.UTF8.GetString(bytes);
        if (utf8.Contains('\uFFFD', StringComparison.Ordinal) && LooksLikeLatin1(bytes))
            return Encoding.Latin1.GetString(bytes);
        return utf8;
    }

    private static TouristTaxExtraction Milano(string text)
    {
        var slice = SliceAfter(text, "Tariffe dal 1", until: "Tariffe dal 1") is { Length: > 80 } april
            && april.Contains("aprile 2026", StringComparison.OrdinalIgnoreCase)
            ? april
            : text;
        if (!TryAmountAfter(slice, LocazioniBreviMilano(), out var amount))
            return TouristTaxExtraction.Failed("milano_locazioni_brevi_amount_not_found");

        return TouristTaxExtraction.Ok(
            [Fixed("015146", "Milano", "LOM", amount, maxNights: null, category: null,
                new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))],
            $"locazioni brevi {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Roma(string text)
    {
        if (!TryAmountAfter(text, RomaLocazioneBreve(), out var breve)
            || !TryAmountAfter(text, RomaCav1(), out var cav1)
            || !TryAmountAfter(text, RomaCav2(), out var cav2))
            return TouristTaxExtraction.Failed("roma_locazioni_or_cav_amount_not_found");

        var from = new DateTime(2023, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        return TouristTaxExtraction.Ok(
            [
                Fixed("058091", "Roma", "LAZ", cav1, 10,
                    "Case e appartamenti per vacanze, categoria 1", from),
                Fixed("058091", "Roma", "LAZ", cav2, 10,
                    "Case e appartamenti per vacanze, categoria 2", from),
                Fixed("058091", "Roma", "LAZ", breve, 10,
                    "Immobili destinati alla locazione breve", from),
            ],
            $"locazione breve {breve.ToString("0.00", CultureInfo.InvariantCulture)} EUR; CAV1 {cav1.ToString("0.00", CultureInfo.InvariantCulture)}; CAV2 {cav2.ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    private static TouristTaxExtraction Como(string text)
    {
        if (!TryAmountAfter(text, ComoLocazioni(), out var amount))
            return TouristTaxExtraction.Failed("como_locazioni_brevi_amount_not_found");

        int? maxNights = ComoMaxNights().Match(text) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, out var nights)
            ? nights
            : null;
        return TouristTaxExtraction.Ok(
            [Fixed("013075", "Como", "LOM", amount, maxNights, category: null,
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))],
            $"locazioni brevi {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Firenze(string text)
    {
        if (!TryAmountAfter(text, FirenzeLocazioni(), out var amount))
            return TouristTaxExtraction.Failed("firenze_locazioni_amount_not_found");

        return TouristTaxExtraction.Ok(
            [Fixed("048017", "Firenze", "TOS", amount, maxNights: null, category: null,
                new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc))],
            $"locazioni turistiche {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Napoli(string text)
    {
        var match = NapoliLocazioni().Match(text);
        if (!match.Success || !TryEuro(match.Groups[1].Value, out var first))
            return TouristTaxExtraction.Failed("napoli_locazioni_brevi_amount_not_found");

        // Two columns (01/01/2026 then 01/05/2026): the in-force rate is the last one.
        var amount = match.Groups[2].Success && TryEuro(match.Groups[2].Value, out var second) ? second : first;
        return TouristTaxExtraction.Ok(
            [Fixed("063049", "Napoli", "CAM", amount, maxNights: 14, category: null,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc))],
            $"locazioni brevi {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Torino(string text)
    {
        if (!TryAmountAfter(text, TorinoLocazioni(), out var amount))
            return TouristTaxExtraction.Failed("torino_locazioni_brevi_amount_not_found");

        return TouristTaxExtraction.Ok(
            [Fixed("001272", "Torino", "PIE", amount, maxNights: 7, category: null,
                new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))],
            $"locazioni turistiche e brevi {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Venezia(string text)
    {
        var locazioniGruppo = Regex.Match(
            text,
            @"GRUPPO\s*1\s*:[\s\S]{0,80}?A/1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!locazioniGruppo.Success)
            return TouristTaxExtraction.Failed("venezia_locazioni_section_not_found");

        var slice = text[locazioniGruppo.Index..];
        var bassaAt = slice.IndexOf("BASSA STAGIONE", StringComparison.OrdinalIgnoreCase);
        if (bassaAt < 0)
            return TouristTaxExtraction.Failed("venezia_bassa_stagione_not_found");

        if (!TryVeneziaGroups(slice[..bassaAt], out var g1High, out var g1HighR, out var g2High, out var g2HighR, out var g3High, out var g3HighR)
            || !TryVeneziaGroups(slice[bassaAt..], out var g1Low, out var g1LowR, out var g2Low, out var g2LowR, out var g3Low, out var g3LowR))
            return TouristTaxExtraction.Failed("venezia_gruppo_amounts_not_found");

        return TouristTaxExtraction.Ok(
            [
                VeneziaRate("Gruppo 1 (categorie catastali A/1, A/8, A/9)", "02-01", "12-31", g1High, g1HighR),
                VeneziaRate("Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)", "02-01", "12-31", g2High, g2HighR),
                VeneziaRate("Gruppo 3 (categorie catastali A/4, A/5)", "02-01", "12-31", g3High, g3HighR),
                VeneziaRate("Gruppo 1 (categorie catastali A/1, A/8, A/9)", "01-01", "01-31", g1Low, g1LowR),
                VeneziaRate("Gruppo 2 (categorie catastali A/2, A/3, A/6, A/7, A/11)", "01-01", "01-31", g2Low, g2LowR),
                VeneziaRate("Gruppo 3 (categorie catastali A/4, A/5)", "01-01", "01-31", g3Low, g3LowR),
            ],
            $"locazioni turistiche alta G1-G3 {g1High.ToString("0.00", CultureInfo.InvariantCulture)}/{g2High.ToString("0.00", CultureInfo.InvariantCulture)}/{g3High.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static TouristTaxExtraction Bologna(string text)
    {
        if (!text.Contains("locazione breve", StringComparison.OrdinalIgnoreCase))
            return TouristTaxExtraction.Failed("bologna_locazione_breve_not_found");

        var match = BolognaPercent().Match(text);
        if (!match.Success
            || !TryEuro(match.Groups[1].Value, out var percent)
            || !TryEuro(match.Groups[2].Value, out var cap))
            return TouristTaxExtraction.Failed("bologna_percent_or_cap_not_found");

        return TouristTaxExtraction.Ok(
            [
                new ExtractedTouristTaxRate(
                    "037006",
                    "Bologna",
                    "EMR",
                    AccommodationCategory: null,
                    SeasonStart: null,
                    SeasonEnd: null,
                    TouristTaxCalculationMethod.PercentOfNightlyPrice,
                    RatePerPersonPerNight: 0m,
                    PercentOfNightlyPrice: percent,
                    CapPerPersonPerNight: cap,
                    MaxNights: null,
                    ReducedRateMaxAge: null,
                    ReducedRatePerPersonPerNight: null,
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ],
            $"locazioni brevi {percent.ToString("0.00", CultureInfo.InvariantCulture)}% cap {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR");
    }

    private static ExtractedTouristTaxRate Fixed(
        string istat, string city, string region, decimal amount, int? maxNights, string? category, DateTime effectiveFrom) =>
        new(istat, city, region, category, null, null, TouristTaxCalculationMethod.PerPersonPerNight, amount,
            null, null, maxNights, null, null, effectiveFrom);

    private static ExtractedTouristTaxRate VeneziaRate(
        string category, string seasonStart, string seasonEnd, decimal amount, decimal reduced) =>
        new("027042", "Venezia", "VEN", category, seasonStart, seasonEnd,
            TouristTaxCalculationMethod.PerPersonPerNight, amount, null, null, 5, 16, reduced,
            new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc));

    private static bool TryVeneziaGroups(
        string slice,
        out decimal g1, out decimal g1r, out decimal g2, out decimal g2r, out decimal g3, out decimal g3r)
    {
        g1 = g1r = g2 = g2r = g3 = g3r = 0;
        return TryGroupPair(slice, 1, out g1, out g1r)
            && TryGroupPair(slice, 2, out g2, out g2r)
            && TryGroupPair(slice, 3, out g3, out g3r);
    }

    private static bool TryGroupPair(string slice, int group, out decimal full, out decimal reduced)
    {
        reduced = 0;
        var match = new Regex(
            $@"GRUPPO\s*{group}\s*:[\s\S]{{0,80}}?(\d+[.,]\d{{2}})[^\d]{{0,20}}(\d+[.,]\d{{2}})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(slice);
        if (!match.Success || !TryEuro(match.Groups[1].Value, out full) || !TryEuro(match.Groups[2].Value, out reduced))
        {
            full = 0;
            return false;
        }

        return true;
    }

    private static bool TryAmountAfter(string text, Regex label, out decimal amount)
    {
        amount = 0;
        var match = label.Match(text);
        if (!match.Success)
            return false;
        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success && TryEuro(match.Groups[i].Value, out amount))
                return true;
        }

        return false;
    }

    private static bool TryEuro(string raw, out decimal amount)
    {
        var token = raw.Trim().Replace(',', '.');
        if (token.IndexOf('.', StringComparison.Ordinal) < 0)
            token += ".00";
        return decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
            && amount > 0m
            && amount < 100m;
    }

    private static string SliceAfter(string text, string start, string until)
    {
        var from = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0)
            return text;
        var rest = text[from..];
        var next = rest.IndexOf(until, start.Length, StringComparison.OrdinalIgnoreCase);
        return next < 0 ? rest : rest[..next];
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousSpace = true;
        foreach (var ch in text)
        {
            var mapped = ch switch
            {
                '\u00A0' or '\u202F' or '\t' or '\r' or '\n' => ' ',
                '\u2013' or '\u2014' => '-',
                '\u2018' or '\u2019' or '\u201C' or '\u201D' => '\'',
                _ => ch,
            };
            if (char.IsWhiteSpace(mapped))
            {
                if (previousSpace)
                    continue;
                builder.Append(' ');
                previousSpace = true;
                continue;
            }

            builder.Append(mapped);
            previousSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool IsPdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';

    private static bool LooksLikeLatin1(ReadOnlySpan<byte> bytes)
    {
        var high = 0;
        foreach (var b in bytes)
        {
            if (b >= 0x80)
                high++;
        }

        return high > 0;
    }

    [GeneratedRegex(@"Locazioni\s+Brevi(?:\s+ex\s+D\.?\s*L\.?\s*n?\.?\s*50/2017)?[^\d]{0,20}(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocazioniBreviMilano();

    [GeneratedRegex(@"Immobili\s+destinati\s+alla\s+locazione\s+breve[\s\S]{0,200}?(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RomaLocazioneBreve();

    [GeneratedRegex(@"Case\s+e\s+Appartamenti\s+per\s+vacanze\s*[-,]?\s*Categoria\s*1[\s\S]{0,40}?(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RomaCav1();

    [GeneratedRegex(@"Case\s+e\s+Appartamenti\s+per\s+vacanze\s*[-,]?\s*Categoria\s*2[\s\S]{0,40}?(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RomaCav2();

    [GeneratedRegex(@"(?:appartamenti\s+per\s+vacanze\s*\(CAV\)[\s\S]{0,50}?(\d+[.,]\d{2})[\s\S]{0,120}?locazioni\s+turistiche\s+e\s+locazioni\s+brevi|locazioni\s+turistiche\s+e\s+locazioni\s+brevi[^\d]{0,20}(\d+[.,]\d{2}))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComoLocazioni();

    [GeneratedRegex(@"massimo\s+di\s+(\d+)\s+giorni", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComoMaxNights();

    [GeneratedRegex(@"Locazioni\s+di\s+immobili\s+ad\s+uso(?:\s+turistico)?[\s\S]{0,30}?(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirenzeLocazioni();

    [GeneratedRegex(@"Locazioni\s+brevi[^\d]{0,40}(\d+[.,]\d{2})(?:[^\d]{0,40}(\d+[.,]\d{2}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NapoliLocazioni();

    [GeneratedRegex(@"Locazioni\s+Turistiche\s+e\s+Brevi[^\d]{0,20}(\d+[.,]\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TorinoLocazioni();

    [GeneratedRegex(@"percentuale\s+del\s+(\d+[.,]\d{2})\s*%[\s\S]{0,220}?limite\s+massimo\s+di\s+(\d+(?:[.,]\d{2})?)\s+euro\s+a\s+persona", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BolognaPercent();
}

public sealed record TouristTaxExtraction(bool Succeeded, string Detail, IReadOnlyList<ExtractedTouristTaxRate> Rates)
{
    public static TouristTaxExtraction Failed(string detail) => new(false, detail, []);

    public static TouristTaxExtraction Ok(IReadOnlyList<ExtractedTouristTaxRate> rates, string detail) =>
        new(true, detail, rates);
}

public sealed record ExtractedTouristTaxRate(
    string IstatCode,
    string City,
    string RegionCode,
    string? AccommodationCategory,
    string? SeasonStart,
    string? SeasonEnd,
    TouristTaxCalculationMethod CalculationMethod,
    decimal RatePerPersonPerNight,
    decimal? PercentOfNightlyPrice,
    decimal? CapPerPersonPerNight,
    int? MaxNights,
    int? ReducedRateMaxAge,
    decimal? ReducedRatePerPersonPerNight,
    DateTime EffectiveFrom);
