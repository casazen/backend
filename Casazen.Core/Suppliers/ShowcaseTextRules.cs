using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The checks of what the AI wrote for the showcase of a supplier: the same rules as the static starter (SP-13d, VT-D20, see
/// <c>starter-content.ts</c>) applied to a text nobody reviewed. A text of CasaZen on the page of a supplier says only what is true by the nature
/// of the job, so it never carries a guarantee, a certification, an insurance, a number of years, a time, a price, a rating, a promise or an
/// adjective of quality that nobody measured. The one exception is what the <b>supplier</b> wrote (its answers, the names of its services, the
/// texts already in its draft): a claim of that kind that the supplier made itself may be repeated, and only that one.
/// </summary>
/// <remarks>
/// The check is deliberately blunt: a field with a doubtful word is dropped (<see cref="ShowcaseTextOutcome.Rejected"/>) and the supplier
/// asks again or writes it. A text that is a little too cautious is better than a claim on a public page that the supplier never made. The words
/// are matched on lower-case text without accents, so «Garanzia», «garanzìa» and «GARANZIA» are the same.
/// </remarks>
public static class ShowcaseTextRules
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Numbers = Rx(@"\d+");
    private static readonly Regex ListMarker = Rx(@"^\s*(?:[-–•*]|\d+[.)])\s*");

    /// <summary>
    /// The kinds of claim a text may not make unless the supplier made it. <c>SupplierMayOwn</c> false is for what the supplier cannot own
    /// either: contact data and long digit runs never belong in a text of CasaZen.
    /// </summary>
    private static readonly (string Label, Regex Pattern, bool SupplierMayOwn)[] Claims =
    [
        ("guarantee", Rx(@"\bgaranz\w*|\bgarantit\w*|\bgarantisc\w*|\bgarantiam\w*"), true),
        ("certification", Rx(@"\bcertific\w*|\biso\s?\d+|\baccredit\w*|\babilitat\w*"), true),
        ("insurance", Rx(@"\bassicur\w*|\bpolizz\w*"), true),
        ("rating", Rx(@"\brecension\w*|\bvalutazion\w*|\bstell[ae]\b|\bfeedback\b|\brating\b|\bvoto\b"), true),
        (
            "time",
            Rx(@"\b24\s?(?:h|ore)\b|\bh24\b|\b(?:in|entro)\s+(?:\d+|un|una|mezz[ao])\s*(?:ore|ora|minuti|minuto|giorni|giorno|settimane)\b|\btempestiv\w*|\bimmediat\w*|\bin giornata\b|\bstesso giorno\b|\bsame day\b|\bsempre disponibil\w*|\b7\s?su\s?7\b"),
            true),
        (
            "experience",
            Rx(@"\banni\s+di\s+(?:esperienza|attivita|lavoro)\b|\besperienz\w*|\bdecenn\w*|\bventenn\w*|\btrentenn\w*|\bdal\s+(?:19|20)\d{2}\b|\bda oltre\b|\bda piu di\b|\bstoric\w*"),
            true),
        (
            "price",
            Rx(@"€|\beuro\b|\bprezz\w*|\btariff\w*|\bsconto\w*|\bscontat\w*|\bgratuit\w*|\bgratis\b|\beconomic\w*|\bconvenient\w*|\bofferta\b|\bpromozion\w*"),
            true),
        (
            "quality",
            Rx(@"\bprofessional\w*|\besper[tio]\w*|\bqualificat\w*|\baffidabil\w*|\bpuntual\w*|\brapid\w*|\bveloc\w*|\bimpeccabil\w*|\beccellen\w*|\bmigliore\b|\bmigliori\b|\bottim\w*|\bsuperb\w*|\bperfett\w*|\bmassim\w*|\bleader\b|\btop\b|\bnumero uno\b|\bfiduc\w*|\bfidat\w*|\bserieta\b|\baccurat\w*|\bscrupol\w*|\bcompeten\w*|\bappassionat\w*|\bdedizion\w*|\bcon (?:cura|passione|attenzione)\b"),
            true),
        ("platform", Rx(@"\bverificat\w*|\bufficial\w*|\bpartner\w*"), true),
        ("contact", Rx(@"https?:|www\.|@|\[(?:tel|email|cf)\]|\d{6,}"), false),
    ];

    /// <summary>
    /// Keeps of the answer of the model (<paramref name="aiContent"/>, a JSON object) the members of <paramref name="fields"/> that fit their
    /// limits, are plain text and make no claim the supplier did not make. <paramref name="supplierWritten"/> is everything the supplier wrote:
    /// the only place a claim of the forbidden kinds may come from.
    /// </summary>
    public static ShowcaseTextOutcome Check(string? aiContent, IReadOnlyCollection<string> fields, IEnumerable<string?> supplierWritten)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(supplierWritten);

        var root = ReadObject(aiContent);
        if (root is null)
            return new ShowcaseTextOutcome(null, null, null, [], fields.ToList());

        var written = NormalizeSources(supplierWritten);
        var rejected = new List<string>();
        string? tagline = null;
        string? intro = null;
        string? bio = null;
        IReadOnlyList<string> prep = [];

        foreach (var field in fields)
        {
            switch (field)
            {
                case ShowcaseTextFields.Tagline:
                    tagline = Accept(ReadString(root, field), ShowcaseContentLimits.TaglineMax, paragraphs: false, isTagline: true, written);
                    if (tagline is null)
                        rejected.Add(field);
                    break;
                case ShowcaseTextFields.Intro:
                    intro = Accept(ReadString(root, field), ShowcaseContentLimits.IntroMax, paragraphs: false, isTagline: false, written);
                    if (intro is null)
                        rejected.Add(field);
                    break;
                case ShowcaseTextFields.Bio:
                    bio = Accept(ReadString(root, field), ShowcaseContentLimits.BioMax, paragraphs: true, isTagline: false, written);
                    if (bio is null)
                        rejected.Add(field);
                    break;
                case ShowcaseTextFields.Prep:
                    prep = AcceptPrep(ReadList(root, field), written);
                    if (prep.Count == 0)
                        rejected.Add(field);
                    break;
            }
        }

        return new ShowcaseTextOutcome(tagline, intro, bio, prep, rejected);
    }

    /// <summary>
    /// The first kind of claim <paramref name="text"/> makes that the supplier did not make (<c>guarantee</c>, <c>certification</c>,
    /// <c>insurance</c>, <c>rating</c>, <c>time</c>, <c>experience</c>, <c>price</c>, <c>quality</c>, <c>platform</c>, <c>contact</c>, or
    /// <c>number</c> for a figure nobody gave), or <c>null</c> when it makes none.
    /// </summary>
    public static string? FindUnsupportedClaim(string? text, IEnumerable<string?> supplierWritten) =>
        FindUnsupportedClaim(text, NormalizeSources(supplierWritten));

    private static string? FindUnsupportedClaim(string? text, string normalizedWritten)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = Normalize(text);
        foreach (var (label, pattern, supplierMayOwn) in Claims)
        {
            foreach (Match match in pattern.Matches(normalized))
            {
                if (supplierMayOwn && normalizedWritten.Contains(Needle(match.Value), StringComparison.Ordinal))
                    continue;

                return label;
            }
        }

        // A figure nobody gave: a year, a count, an hour.
        var allowed = Numbers.Matches(normalizedWritten).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        return Numbers.Matches(normalized).Any(match => !allowed.Contains(match.Value)) ? "number" : null;
    }

    /// <summary>
    /// What to look for in the text of the supplier to know it wrote the same thing: the whole match for an expression of several words, the
    /// first five letters for a single word (so «certificati» covers «certificazione»).
    /// </summary>
    private static string Needle(string match)
    {
        var trimmed = match.Trim();
        return trimmed.Contains(' ', StringComparison.Ordinal) || trimmed.Length <= 5 ? trimmed : trimmed[..5];
    }

    private static string? Accept(string? raw, int max, bool paragraphs, bool isTagline, string written)
    {
        var text = paragraphs ? ShowcaseText.Paragraphs(raw) : ShowcaseText.Line(raw);
        if (text is null)
            return null;

        text = Tidy(text, isTagline);
        if (text.Length == 0 || ShowcaseText.LooksLikeMarkup(text) || HasPictograph(text))
            return null;

        if (text.Length > max)
        {
            // A long "chi siamo" is cut at the end of a sentence; a sentence or an introduction over its limit is asked again.
            text = paragraphs ? CutAtSentence(text, max) ?? string.Empty : string.Empty;
            if (text.Length == 0)
                return null;
        }

        return FindUnsupportedClaim(text, written) is null ? text : null;
    }

    private static IReadOnlyList<string> AcceptPrep(IReadOnlyList<string> raw, string written)
    {
        var kept = new List<string>();
        foreach (var item in raw)
        {
            var text = ShowcaseText.Line(ListMarker.Replace(item, string.Empty));
            if (text is null)
                continue;

            text = Tidy(text, isTagline: false);
            if (text.Length == 0
                || text.Length > ShowcaseContentLimits.PrepItemMax
                || ShowcaseText.LooksLikeMarkup(text)
                || HasPictograph(text)
                || FindUnsupportedClaim(text, written) is not null
                || kept.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(text);
            if (kept.Count == ShowcaseContentLimits.PrepMax)
                break;
        }

        return kept;
    }

    /// <summary>The small corrections that make a text fit the page: no exclamation marks, no quotes around it, no full stop after the sentence in large type.</summary>
    private static string Tidy(string text, bool isTagline)
    {
        var tidy = text.Replace('!', '.');
        tidy = Regex.Replace(tidy, @"\.{2,}", ".", RegexOptions.CultureInvariant, MatchTimeout).Trim();
        tidy = tidy.Trim('"', '«', '»', '“', '”').Trim();
        return isTagline ? tidy.TrimEnd('.').Trim() : tidy;
    }

    /// <summary>The text up to its last sentence that ends within <paramref name="max"/> characters, or <c>null</c> when that leaves less than half.</summary>
    private static string? CutAtSentence(string text, int max)
    {
        var window = text[..max];
        var cut = window.LastIndexOfAny(['.', '?']);
        return cut >= max / 2 ? window[..(cut + 1)].TrimEnd() : null;
    }

    private static bool HasPictograph(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is (>= 0x1F000 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF))
                return true;
        }

        return false;
    }

    private static JsonObject? ReadObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // The model is asked for a bare JSON object, and some answer with a sentence or a code fence around it.
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonNode.Parse(content[start..(end + 1)]) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IReadOnlyList<string> ReadList(JsonObject root, string name) =>
        root[name] is JsonArray array
            ? array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList()
            : [];

    private static string NormalizeSources(IEnumerable<string?> supplierWritten) =>
        Normalize(string.Join('\n', supplierWritten.Where(text => !string.IsNullOrWhiteSpace(text))));

    /// <summary>Lower case, no accents, straight apostrophes: the form the words of <see cref="Claims"/> are matched in.</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(c is '’' or '‘' ? '\'' : char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant, MatchTimeout);
}
