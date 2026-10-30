using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

public static partial class LeaseContractTextParser
{
    // ---- the type ----------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"transitori[oa]|esigenze\s+transitorie", Options, Timeout)]
    private static partial Regex TransitorySignal();

    // "non ha natura transitoria": the word is there to say it is not.
    [GeneratedRegex(@"\bnon\b(?:\W+\w+){0,3}?\W+transitori", Options, Timeout)]
    private static partial Regex NotTransitory();

    [GeneratedRegex(
        @"canone\s+concordato|contratto\s+(?:di\s+locazione\s+)?concordato|locazione\s+abitativa\s+agevolata|canone\s+agevolato|accordi?\s+territorial[ei]|(?<![\d+])3\s?\+\s?2(?!\d)|tre\s?\+\s?due|art(?:icolo|\.)?\s*2\s*,?\s*(?:comma|c\.)\s*3\b",
        Options,
        Timeout)]
    private static partial Regex ConcordatoSignal();

    [GeneratedRegex(
        @"(?<![\d+])4\s?\+\s?4(?!\d)|quattro\s?\+\s?quattro|art(?:icolo|\.)?\s*2\s*,?\s*(?:comma|c\.)\s*1\b|canone\s+libero|contratto\s+libero|libero\s+mercato",
        Options,
        Timeout)]
    private static partial Regex LiberoSignal();

    // The student lease is not a type of CasaZen: nothing is read, the person chooses.
    [GeneratedRegex(@"studenti\s+universitari|art\.?\s*5\s*,?\s*(?:comma|c\.)\s*2\b", Options, Timeout)]
    private static partial Regex StudentSignal();

    private static int? FirstSignal(string flat, Regex signal, bool skipNegated = false)
    {
        foreach (Match match in signal.Matches(flat))
        {
            if (skipNegated)
            {
                var from = Math.Max(0, match.Index - 40);
                if (NotTransitory().IsMatch(flat.Substring(from, match.Index + match.Length - from)))
                    continue;
            }

            return match.Index;
        }

        return null;
    }

    /// <summary>
    /// The type the contract names: transitory (and not longer than eighteen months), concordato or libero, from the words it uses
    /// ("canone concordato", "3+2", "4+4", "art. 2 comma 1"…). A word in the first lines, where a contract says what it is, weighs more than
    /// one further on; two different types named are read as the one named first, with less certainty; none named, the length of the
    /// term (four years, three years) gives it, marked as worked out.
    /// </summary>
    private static ReadValue<LeaseContractType>? ReadContractType(string flat, int? months)
    {
        const int head = 2500;

        if (StudentSignal().IsMatch(flat))
            return null;

        if (FirstSignal(flat, TransitorySignal(), skipNegated: true) is { } transitory
            && (months is null || months <= LeaseContractTerms.TransitorioMaximumMonths))
        {
            return new ReadValue<LeaseContractType>(LeaseContractType.Transitorio, transitory < head ? 90 : 78);
        }

        var concordato = FirstSignal(flat, ConcordatoSignal());
        var libero = FirstSignal(flat, LiberoSignal());
        if (concordato is { } c && libero is { } l)
            return new ReadValue<LeaseContractType>(c < l ? LeaseContractType.Concordato : LeaseContractType.Libero, 72);
        if (concordato is { } onlyConcordato)
            return new ReadValue<LeaseContractType>(LeaseContractType.Concordato, onlyConcordato < head ? 90 : 78);
        if (libero is { } onlyLibero)
            return new ReadValue<LeaseContractType>(LeaseContractType.Libero, onlyLibero < head ? 90 : 78);

        return months switch
        {
            LeaseContractTerms.LiberoMinimumMonths => new ReadValue<LeaseContractType>(LeaseContractType.Libero, 72, Derived: true),
            LeaseContractTerms.ConcordatoMinimumMonths => new ReadValue<LeaseContractType>(LeaseContractType.Concordato, 72, Derived: true),
            _ => null,
        };
    }

    // ---- the tax regime ----------------------------------------------------------------------------------------------------

    private const string ChoiceVerbs = @"opt\w*|avval\w+|intend\w+|applic\w+|ader\w+|scelt\w+|esercit\w+|soggett\w+|usufru\w+|beneficia\w*";

    [GeneratedRegex(@"cedolare\s+secca", Options, Timeout)]
    private static partial Regex CedolareMention();

    [GeneratedRegex(@"\bnon\b[^.;]{0,60}?\b(?:" + ChoiceVerbs + @")\b", Options, Timeout)]
    private static partial Regex NotChoosing();

    [GeneratedRegex(@"\b(?:" + ChoiceVerbs + @"|opzione|regime\s+della)\b", Options, Timeout)]
    private static partial Regex Choosing();

    [GeneratedRegex(@"^\W{0,3}(?:[^.;]{0,25}?\b)?(?:scelta|prescelta|esercitata|applicata)\b", Options, Timeout)]
    private static partial Regex ChosenAfter();

    [GeneratedRegex(@"\b(?:qualora|ove|nel\s+caso\s+in\s+cui|se\s+il\s+locatore|laddove|eventualmente|in\s+caso\s+di|potr[\u00e0a]|potrebbe)\b", Options, Timeout)]
    private static partial Regex ConditionalWord();

    [GeneratedRegex(@"regime\s+ordinario|tassazione\s+ordinaria|ordinari[oa]\s+(?:regime|tassazione|irpef)|regime\s+fiscale\s+ordinario", Options, Timeout)]
    private static partial Regex OrdinaryMention();

    [GeneratedRegex(@"in\s+luogo\s+del|anzich[e\u00e9\u00e8]|invece\s+del|rispetto\s+al|diverso\s+dal", Options, Timeout)]
    private static partial Regex InsteadOf();

    /// <summary>
    /// "Il Locatore ha optato per la cedolare secca" or "non ha optato": the sentence that says what the landlord chose. A mention that
    /// is a condition ("qualora opti per…"), only a reference, or both a yes and a no, says nothing.
    /// </summary>
    private static ReadValue<LeaseTaxRegime>? ReadTaxRegime(string flat)
    {
        var cedolare = 0;
        var ordinary = 0;

        foreach (Match mention in CedolareMention().Matches(flat))
        {
            var before = flat[Math.Max(0, mention.Index - 110)..mention.Index];
            var sentence = before.LastIndexOf(". ", StringComparison.Ordinal);
            if (sentence >= 0)
                before = before[(sentence + 2)..];

            if (ConditionalWord().IsMatch(before.Length > 70 ? before[^70..] : before))
                continue;

            var end = mention.Index + mention.Length;
            if (NotChoosing().IsMatch(before))
                ordinary++;
            else if (Choosing().IsMatch(before) || ChosenAfter().IsMatch(flat.Substring(end, Math.Min(60, flat.Length - end))))
                cedolare++;
        }

        foreach (Match mention in OrdinaryMention().Matches(flat))
        {
            var before = flat[Math.Max(0, mention.Index - 40)..mention.Index];
            if (!InsteadOf().IsMatch(before) && !ConditionalWord().IsMatch(before))
                ordinary++;
        }

        if (cedolare > 0 && ordinary == 0)
            return new ReadValue<LeaseTaxRegime>(LeaseTaxRegime.CedolareSecca, 85);
        if (ordinary > 0 && cedolare == 0)
            return new ReadValue<LeaseTaxRegime>(LeaseTaxRegime.Ordinario, 85);
        return null;
    }

    // ---- rent and deposit --------------------------------------------------------------------------------------------------

    private readonly record struct Amount(decimal Value, int Index, int End);

    [GeneratedRegex(@"(?:\u20ac|\beuro\b|\beur\b\.?)\s*(?<n>\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?!\d)", Options, Timeout)]
    private static partial Regex AmountAfterCurrency();

    [GeneratedRegex(@"(?<![\d.,])(?<n>\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)\s*(?:\u20ac|\beuro\b|\beur\b)", Options, Timeout)]
    private static partial Regex AmountBeforeCurrency();

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex ThousandsOnly();

    [GeneratedRegex(@"^\d+\.\d{1,2}$", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex CentsOnly();

    /// <summary>An amount as an Italian contract writes it ("1.200,00", "800,00", "800") or as an OCR leaves it ("800.00", "1 200,00"); <c>null</c> if it is not one.</summary>
    internal static decimal? ParseAmount(string raw)
    {
        var text = raw.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (text.Contains('.', StringComparison.Ordinal) && text.Contains(',', StringComparison.Ordinal))
        {
            text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }
        else if (text.Contains(',', StringComparison.Ordinal))
        {
            if (text.Count(c => c == ',') > 1)
                return null;
            text = text.Replace(',', '.');
        }
        else if (text.Contains('.', StringComparison.Ordinal))
        {
            if (ThousandsOnly().IsMatch(text))
                text = text.Replace(".", string.Empty, StringComparison.Ordinal);
            else if (!CentsOnly().IsMatch(text))
                return null;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    private static List<Amount> FindAmounts(string flat)
    {
        var amounts = new List<Amount>();

        void Add(Match match)
        {
            if (ParseAmount(match.Groups["n"].Value) is not { } value)
                return;

            var amount = new Amount(value, match.Index, match.Index + match.Length);
            if (!amounts.Any(a => a.Index < amount.End && amount.Index < a.End))
                amounts.Add(amount);
        }

        foreach (Match match in AmountAfterCurrency().Matches(flat))
            Add(match);
        foreach (Match match in AmountBeforeCurrency().Matches(flat))
            Add(match);

        return amounts.OrderBy(a => a.Index).ToList();
    }

    private static Amount? FirstAmountFrom(List<Amount> amounts, int from)
    {
        foreach (var amount in amounts)
        {
            if (amount.Index >= from)
                return amount;
        }

        return null;
    }

    [GeneratedRegex(
        @"\b(?:canone(?:\s+(?:mensile|annuo|annuale|di\s+locazione|locativo|di\s+affitto|iniziale|pattuito|convenuto|concordato))*|rat[ae]\s+mensil[ei]|importo\s+mensile|pigione|corrispettivo(?:\s+(?:mensile|annuo))?)\b",
        Options,
        Timeout)]
    private static partial Regex RentLabel();

    // "il canone annuo di locazione, … è convenuto in EUR 7.140,00": the label, whatever clause comes next, and "convenuto in" before the amount.
    [GeneratedRegex(
        @"\b(?<label>canone(?:\s+(?:mensile|annuo|annuale|di\s+locazione|locativo|iniziale|pattuito|convenuto|concordato))*)\b(?:(?!\bcanone\b)[^.;]){0,300}?\b(?:\p{L}{1,5}\s+){0,2}(?:convenut[oa]|pattuit[oa]|stabilit[oa]|determinat[oa]|fissat[oa])\s+(?:in|di|nella\s+misura\s+di|pari\s+a)\s+(?:\p{Sc}|\beuro\b|\beur\b\.?)\s*(?<n>\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?!\d)",
        Options,
        Timeout)]
    private static partial Regex RentConvened();

    // "in n. 12 (dodici) rate eguali anticipate di EUR 595,00 (cinquecentonovantacinque/00) ciascuna"
    [GeneratedRegex(
        @"(?<![\p{L}\d])(?<count>\d{1,2})\s*(?:\([^)]{0,25}\)\s*)?rate\b[^.;]{0,60}?\bdi\s+(?:\p{Sc}|\beuro\b|\beur\b\.?)\s*(?<n>\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?!\d)",
        Options,
        Timeout)]
    private static partial Regex Installments();

    [GeneratedRegex(@"\b(?:deposito\s+cauzionale|deposito\s+di\s+garanzia|deposito\s+infruttifero|cauzione)\b", Options, Timeout)]
    private static partial Regex DepositLabel();

    // What, between the label and the amount, says the amount is not the rent.
    [GeneratedRegex(
        @"\b(?:deposito|cauzion\w*|spese|oneri|imposta|registro|bollo|istat|aggiornament\w*|mora|interess\w*|penale|caparra|condominial\w*|utenze|acconto)\b",
        Options,
        Timeout)]
    private static partial Regex RentStopWords();

    // ... and not the deposit ("canone": an amount after "tre mensilita del canone" is the rent).
    [GeneratedRegex(
        @"\b(?:spese|oneri|imposta|registro|bollo|istat|aggiornament\w*|mora|interess\w*|penale|caparra|condominial\w*|utenze|acconto|canone)\b",
        Options,
        Timeout)]
    private static partial Regex DepositStopWords();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Parenthetical();

    // The end of a sentence: a full stop, a space and a capital.
    [GeneratedRegex(@"\.\s+\p{Lu}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"annu\w*|\banno\b|\banni\b", Options, Timeout)]
    private static partial Regex AnnualWord();

    [GeneratedRegex(@"mensil\w*|\bmese\b|\bmesi\b", Options, Timeout)]
    private static partial Regex MonthlyWord();

    // The period named right after the amount: "800,00 mensili", "800,00 (ottocento/00) al mese", "9.600,00 annui".
    [GeneratedRegex(@"^\W{0,3}(?:\([^)]{0,40}\)\W{0,3})?(?<p>al\s+mese|mensil\w*|all'anno|annu\w*|annual\w*|per\s+anno|ogni\s+anno|l'anno)", Options, Timeout)]
    private static partial Regex PeriodAfter();

    [GeneratedRegex(
        @"(?<![\p{L}\d])(?<n>\d{1,2}|un|una|uno|due|tre|quattro|cinque|sei) ?(?:\([^)]{0,25}\) ?)?mensilit[\u00e0a]",
        Options,
        Timeout)]
    private static partial Regex DepositMonths();

    private static bool HasDigitsOutsideParentheses(string gap) => Parenthetical().Replace(gap, " ").Any(char.IsDigit);

    private static int? NumberOf(string word) =>
        int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits) ? digits : NumberWords.TryGetValue(word, out var number) ? number : null;

    private static (ReadValue<decimal>? Rent, ReadValue<decimal>? Deposit) ReadMoney(string flat)
    {
        var amounts = FindAmounts(flat);
        var rent = ReadRent(flat, amounts);
        return (rent, ReadDeposit(flat, amounts, rent));
    }

    /// <summary>
    /// The monthly rent: an amount after "canone" (or "rata mensile") with nothing in between that says it is another sum, and with its
    /// period named ("mensile", "annuo", "al mese"). The annual one and the monthly one agreeing is certain; the annual one alone is
    /// divided by twelve and marked as worked out; two different monthly rents (it changes over the years) or an amount whose period is
    /// not stated are not read.
    /// </summary>
    private static ReadValue<decimal>? ReadRent(string flat, List<Amount> amounts)
    {
        var monthly = new List<decimal>();
        var annual = new List<decimal>();

        foreach (Match label in RentLabel().Matches(flat))
        {
            var from = label.Index + label.Length;
            if (FirstAmountFrom(amounts, from) is not { } amount)
                continue;

            var gap = flat[from..amount.Index];
            if (gap.Length > 110 || RentStopWords().IsMatch(gap) || SentenceEnd().IsMatch(gap.TrimStart('.', ':', '-', ' ')) || HasDigitsOutsideParentheses(gap))
                continue;

            bool? isAnnual = null;
            if (MonthlyWord().IsMatch(label.Value))
                isAnnual = false;
            else if (AnnualWord().IsMatch(label.Value))
                isAnnual = true;
            else if (MonthlyWord().IsMatch(gap))
                isAnnual = false;
            else if (AnnualWord().IsMatch(gap))
                isAnnual = true;
            else if (PeriodAfter().Match(flat.Substring(amount.End, Math.Min(60, flat.Length - amount.End))) is { Success: true } after)
                isAnnual = AnnualWord().IsMatch(after.Groups["p"].Value);

            if (isAnnual is not { } yearly)
                continue;

            // Rents that cannot be one (a registration tax of 67 euro, a million): the label was another sum's.
            if (yearly ? amount.Value is < 360m or > 360_000m : amount.Value is < 30m or > 30_000m)
                continue;

            (yearly ? annual : monthly).Add(amount.Value);
        }

        // "Il canone annuo di locazione, secondo quanto stabilito dall'Accordo … del 20/05/2025 …, è convenuto in EUR 7.140,00": the sentence
        // of the real contracts puts a long clause, with a date, between the label and the amount; "convenuto in" says it is the rent.
        foreach (Match convened in RentConvened().Matches(flat))
        {
            if (ParseAmount(convened.Groups["n"].Value) is not { } value)
                continue;

            var wording = convened.Groups["label"].Value;
            bool? yearly = MonthlyWord().IsMatch(wording)
                ? false
                : AnnualWord().IsMatch(wording)
                    ? true
                    : PeriodAfter().Match(flat.Substring(convened.Index + convened.Length, Math.Min(60, flat.Length - convened.Index - convened.Length))) is { Success: true } named
                        ? AnnualWord().IsMatch(named.Groups["p"].Value)
                        : null;
            if (yearly is not { } isYearly || (isYearly ? value is < 360m or > 360_000m : value is < 30m or > 30_000m))
                continue;

            (isYearly ? annual : monthly).Add(value);
        }

        // "in n. 12 (dodici) rate eguali anticipate di EUR 595,00 ciascuna": twelve installments are twelve months.
        foreach (Match installments in Installments().Matches(flat))
        {
            if (installments.Groups["count"].Value == "12" && ParseAmount(installments.Groups["n"].Value) is { } value && value is >= 30m and <= 30_000m)
                monthly.Add(value);
        }

        var months = monthly.Distinct().ToList();
        var years = annual.Distinct().ToList();
        if (months.Count == 1)
        {
            // The year's rent and the month's agree (up to the cents a division leaves): both are read, and certainly.
            var agrees = years.Count == 1 && Math.Abs(years[0] - (months[0] * 12m)) <= 6m;
            return new ReadValue<decimal>(months[0], agrees ? 95 : years.Count == 0 ? 88 : 75);
        }

        if (months.Count == 0 && years.Count == 1)
            return new ReadValue<decimal>(decimal.Round(years[0] / 12m, 2), 78, Derived: true);

        return null;
    }

    /// <summary>
    /// The security deposit: an amount after "deposito cauzionale" (or "cauzione") before any "N mensilita", else N times the rent — the
    /// rent the contract names after the months, or the one read — marked as worked out. Two different deposits are not read.
    /// </summary>
    private static ReadValue<decimal>? ReadDeposit(string flat, List<Amount> amounts, ReadValue<decimal>? rent)
    {
        var explicitValues = new HashSet<decimal>();
        var workedOut = new HashSet<decimal>();

        foreach (Match label in DepositLabel().Matches(flat))
        {
            var from = label.Index + label.Length;
            var end = Math.Min(flat.Length, from + 340);

            var months = DepositMonths().Match(flat, from, end - from);
            var amount = amounts.Where(a => a.Index >= from && a.Index < end).Select(a => (Amount?)a).FirstOrDefault();
            var count = months.Success ? NumberOf(months.Groups["n"].Value) : null;

            if (amount is { } named && (!months.Success || named.Index < months.Index) && (IsDepositGap(flat[from..named.Index]) || IsSumGap(flat[from..named.Index])))
            {
                explicitValues.Add(named.Value);
                continue;
            }

            if (count is not { } number || number is < 1 or > 12)
                continue;

            // "tre mensilita del canone di euro 800,00": the amount that comes after the months, with "canone" between, is the rent.
            if (amount is { } later && later.Index > months.Index && flat[months.Index..later.Index].Contains("canone", StringComparison.OrdinalIgnoreCase))
                workedOut.Add(number * later.Value);
            else if (rent is not null)
                workedOut.Add(number * rent.Value);
        }

        if (explicitValues.Count == 1)
            return new ReadValue<decimal>(explicitValues.First(), 90);
        if (explicitValues.Count == 0 && workedOut.Count == 1)
            return new ReadValue<decimal>(workedOut.First(), 75, Derived: true);
        return null;
    }

    private static bool IsDepositGap(string gap) =>
        gap.Length <= 100 && !DepositStopWords().IsMatch(gap) && !HasDigitsOutsideParentheses(gap);

    // "Deposito cauzionale e altre forme di garanzia. A garanzia delle obbligazioni assunte col presente contratto, il conduttore versa alla
    // locatrice (…) una somma di EUR 1.785,00": a long sentence that says in the end that the amount is a sum paid.
    [GeneratedRegex(@"\b(?:somma|importo|cifra)\s+(?:complessiva\s+)?(?:di|pari\s+a)\s*$", Options, Timeout)]
    private static partial Regex SumOf();

    private static bool IsSumGap(string gap) =>
        gap.Length <= 330 && SumOf().IsMatch(gap) && !DepositStopWords().IsMatch(gap) && !HasDigitsOutsideParentheses(gap);

    // ---- the property ----------------------------------------------------------------------------------------------------

    // "l'immobile ad uso abitativo sito in Bari, Via Sparano n. 12, piano 2": the clause that says where the property is.
    [GeneratedRegex(
        @"\b(?:immobile|unit[\u00e0a]\s+immobiliare|appartamento|alloggio|abitazione|locali?)\b[^.;]{0,100}?\b(?:sit[oa]|ubicat[oa]|post[oa]|situat[oa])\s+(?:in|a|nel\s+comune\s+di|presso)\s+(?<addr>[^;]{5,170})",
        Options,
        Timeout)]
    private static partial Regex PropertyClause();

    private static readonly HashSet<string> GenericAddressWords = new(StringComparer.Ordinal)
    {
        "VIA", "VIALE", "PIAZZA", "PIAZZALE", "PZA", "CORSO", "LARGO", "VICOLO", "STRADA", "LOCALITA", "LOC", "FRAZIONE", "FRAZ", "NUM", "NUMERO",
        "CIVICO", "SNC", "INT", "INTERNO", "SCALA", "PIANO", "DEL", "DELLA", "DELLO", "DEI", "DEGLI", "DELLE", "ITALIA",
    };

    /// <summary>The words that say where a place is, without the generic ones (via, viale...): the street's name and the number, upper case, no accents.</summary>
    private static HashSet<string> AddressWords(string address)
    {
        var builder = new StringBuilder(address.Length);
        foreach (var c in address.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !GenericAddressWords.Contains(w) && (w.Any(char.IsDigit) || w.Length >= 3))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the property the contract says it lets is the one of the draft: every word of the street and its number is in the clause (match),
    /// or not one of the street's words is (mismatch); anything in between, or no clause, says nothing.
    /// </summary>
    private static ReadValue<PropertyAddressMatch>? ReadProperty(string flat, LeaseContractParseContext? context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.PropertyAddress))
            return null;

        var wanted = AddressWords(context.PropertyAddress);
        if (wanted.Count == 0)
            return null;

        var clause = PropertyClause().Match(flat);
        if (!clause.Success)
            return null;

        var present = AddressWords(CutAtSentenceEnd(clause.Groups["addr"].Value, out _));
        if (wanted.All(present.Contains))
            return new ReadValue<PropertyAddressMatch>(PropertyAddressMatch.Match, 85);

        var names = wanted.Where(w => !w.Any(char.IsDigit)).ToList();
        return names.Count > 0 && !names.Any(present.Contains)
            ? new ReadValue<PropertyAddressMatch>(PropertyAddressMatch.Mismatch, 80)
            : null;
    }
}
