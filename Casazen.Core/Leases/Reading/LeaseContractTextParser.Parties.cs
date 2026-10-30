using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

public static partial class LeaseContractTextParser
{
    // ---- the people --------------------------------------------------------------------------------------------------------
    //
    // A person is found by a fiscal code that passed the check character (ContractFiscalCodes). Before the code the contract introduces
    // them ("Il Sig. Mario Rossi, nato a ..., residente in ..., codice fiscale ..."): the name is the words after the last honorific or
    // label ("Sig.", "Cognome e Nome:") that make a name; after it, "(di seguito Locatore)" says the role. The name is put in order
    // (first name, surname) with the help of the code, whose first six letters are the surname and the name.

    /// <summary>Where a person is introduced: the name before the fiscal code, and where the introduction starts.</summary>
    private sealed record Intro(FoundFiscalCode Code, int Start, List<string> NameWords, PartyRole? LabelRole);

    private const string RolePattern =
        @"locator[ei]|locatrice|locatrici|conduttor[ei]|conduttrice|conduttrici|proprietari[oae]|inquilin[oiae]|locatari[oae]";

    private const string UpperLetter = @"A-Z\u00c0-\u00d6\u00d8-\u00de";
    private const string AnyLetter = @"A-Za-z\u00c0-\u00d6\u00d8-\u00f6\u00f8-\u00ff";

    // An honorific ("Sig.", "Sig.ra", "Sigg.ri", "Signora", "Dott.ssa", "Avv.") or a label that comes before a name; the role words are labels too.
    [GeneratedRegex(
        @"\b(?:(?<hon>sig(?:\.(?:ra|na|ri)?|g\.(?:ri)?|nor[ae]?|nori|ra\b|na\b)(?:\s*/\s*sig\.?(?:ra|na)?)?|dott(?:\.|ssa\b|oressa\b|ore\b)|dr\.|avv(?:\.|ocato\b)|ing\.|ingegnere\b|geom(?:\.|etra\b)|prof(?:\.|essor[ea]?\b)|arch\.|architetto\b|rag\.|ragioniere\b)"
        + @"|(?<lab>cognome\s+e\s+nome|nome\s+e\s+cognome|nominativo|denominazione|generalit[\u00e0a]|(?<role>" + RolePattern + @")))\s*[:\-]?\s*",
        Options,
        Timeout)]
    private static partial Regex NameAnchor();

    // One word of a name: capitalized (or all capitals), or a particle that is followed by one.
    [GeneratedRegex(
        @"\G[ :""\-]*(?<w>[" + UpperLetter + @"][" + AnyLetter + @"'\-]{1,29}|(?:de|di|da|del|della|dello|dei|degli|delle|dal|dalla|dai|la|lo|le|van|von|der|den)(?=\s+[" + UpperLetter + @"])|d'(?=[" + UpperLetter + @"])|dell'(?=[" + UpperLetter + @"]))(?<end>[,;]?)",
        RegexOptions.CultureInvariant,
        Timeout)]
    private static partial Regex NameToken();

    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "codice", "cod", "fiscale", "nato", "nata", "residente", "domiciliato", "domiciliata", "residenza", "via", "viale", "piazza", "corso",
        "locatore", "locatrice", "conduttore", "conduttrice", "il", "ed", "tra", "in", "con", "per", "sig", "cognome", "nome",
        "data", "luogo", "telefono", "tel", "email", "mail", "pec", "documento", "carta", "patente", "passaporto", "cittadinanza",
        "nazionalita", "nazionalit\u00e0", "stato", "civile", "professione", "titolare", "proprietario", "proprietaria", "inquilino", "inquilina",
        "cf", "iva", "piva", "cap", "seguito", "denominato", "denominata", "detto", "detta", "qualita", "qualit\u00e0", "anche", "congiuntamente",
        "dott", "avv", "ing", "geom", "prof", "arch", "rag", "societ\u00e0", "societa", "impresa", "ditta",
    };

    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "di", "da", "del", "della", "dello", "dei", "degli", "delle", "dal", "dalla", "dai", "la", "lo", "le", "van", "von", "der", "den", "d'", "dell'",
    };

    private static List<string> ReadName(string flat, int start, int limit, out int end)
    {
        var words = new List<string>();
        var position = start;
        var ended = false;
        while (!ended && words.Count < 6)
        {
            var match = NameToken().Match(flat, position);
            if (!match.Success || match.Index + match.Length > limit)
                break;

            var word = match.Groups["w"].Value;
            if (NameStopWords.Contains(word))
                break;

            words.Add(word);
            position = match.Index + match.Length;
            ended = match.Groups["end"].Length > 0;
        }

        // A particle at the end belongs to what follows, not to the name.
        while (words.Count > 0 && Particles.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);

        end = position;
        return words;
    }

    private static int RealWords(List<string> words) => words.Count(w => !Particles.Contains(w));

    private static Intro FindIntro(string flat, FoundFiscalCode code, int previousEnd)
    {
        var regionStart = Math.Max(previousEnd, code.Index - 450);
        var anchors = NameAnchor().Matches(flat[regionStart..code.Index]);
        for (var i = anchors.Count - 1; i >= 0; i--)
        {
            var anchor = anchors[i];
            var anchorStart = regionStart + anchor.Index;
            var words = ReadName(flat, anchorStart + anchor.Length, code.Index, out _);
            if (RealWords(words) < 2)
                continue;

            PartyRole? labelRole = anchor.Groups["role"].Success ? RoleOf(anchor.Groups["role"].Value) : null;
            return new Intro(code, anchorStart, words, labelRole);
        }

        // No name before the code: the person is the code alone, and the window to look in starts at the code.
        return new Intro(code, code.Index, [], null);
    }

    private static PartyRole RoleOf(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower.StartsWith("locatari", StringComparison.Ordinal) || lower.StartsWith("conduttor", StringComparison.Ordinal)
            || lower.StartsWith("conduttric", StringComparison.Ordinal) || lower.StartsWith("inquilin", StringComparison.Ordinal)
                ? PartyRole.Tenant
                : PartyRole.Landlord;
    }

    // ---- the role --------------------------------------------------------------------------------------------------------

    // "(di seguito, il Locatore)", "denominato \"Conduttore\"", "in qualita di locatore": after the code.
    [GeneratedRegex(
        @"\b(?:di\s+seguito|d'ora\s+in\s+poi|in\s+seguito|qui\s+di\s+seguito|denominat[oaie]|detto|detta|chiamat[oa]|in\s+qualit[\u00e0a]\s+di|quale|come)\b[^()]{0,40}?\b(?<role>" + RolePattern + @")\b",
        Options,
        Timeout)]
    private static partial Regex RoleDefinition();

    // The role named right before the introduction: "il Locatore Sig. Mario Rossi", "LOCATORE Cognome e Nome: ...".
    [GeneratedRegex(@"\b(?<role>" + RolePattern + @")\W{0,8}$", Options, Timeout)]
    private static partial Regex RoleBefore();

    private static bool IsPluralRole(string word) =>
        word.ToLowerInvariant() is "locatori" or "locatrici" or "conduttori" or "conduttrici" or "inquilini" or "locatari" or "proprietari";

    private sealed record RoleEvidence(PartyRole? Role, int Confidence, bool Plural);

    private static RoleEvidence ResolveRole(string flat, Intro intro, int windowEnd)
    {
        var codeEnd = intro.Code.Index + intro.Code.Length;

        PartyRole? after = null;
        var plural = false;
        if (codeEnd < windowEnd)
        {
            var definition = RoleDefinition().Match(flat[codeEnd..windowEnd]);
            if (definition.Success)
            {
                after = RoleOf(definition.Groups["role"].Value);
                plural = IsPluralRole(definition.Groups["role"].Value) || definition.Value.Contains("congiuntamente", StringComparison.OrdinalIgnoreCase);
            }
        }

        PartyRole? before = intro.LabelRole;
        if (before is null && intro.NameWords.Count > 0)
        {
            var heading = RoleBefore().Match(flat[Math.Max(0, intro.Start - 40)..intro.Start]);
            if (heading.Success)
                before = RoleOf(heading.Groups["role"].Value);
        }

        if (after is not null && before is not null)
            return after == before ? new RoleEvidence(after, 95, plural) : new RoleEvidence(null, 0, false);
        if (after is not null)
            return new RoleEvidence(after, 92, plural);
        if (before is not null)
            return new RoleEvidence(before, 90, false);
        return new RoleEvidence(null, 0, false);
    }

    // ---- name, residence, citizenship ------------------------------------------------------------------------------------

    private static string Title(string word)
    {
        var builder = new StringBuilder(word.Length);
        var startOfWord = true;
        foreach (var c in word)
        {
            builder.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = c is '\'' or '-';
        }

        return builder.ToString();
    }

    private static string JoinName(IEnumerable<string> words) =>
        string.Join(' ', words.Select(w => w.Length > 1 && w == w.ToUpperInvariant() ? Title(w) : w));

    /// <summary>
    /// Puts the words of a name in order — first name, surname — by trying every split and both orders against the first six letters of the
    /// fiscal code. Both letters groups agreeing is certain; one is likely (a married woman keeps her maiden name in the code); neither
    /// leaves the order to the writing (surname first when the line is all capitals) and the name is not taken.
    /// </summary>
    private static (string? First, string? Last, int Confidence) AssignName(List<string> words, string fiscalCode)
    {
        if (RealWords(words) < 2)
            return (null, null, 0);

        var allUpper = words.All(w => w == w.ToUpperInvariant());
        (int Score, int Preference, string First, string Last)? best = null;

        void Consider(string first, string last, bool surnameFirst, int split)
        {
            var score = (ContractFiscalCodes.SurnameCode(last) == fiscalCode[..3] ? 1 : 0)
                        + (ContractFiscalCodes.NameCode(first) == fiscalCode[3..6] ? 1 : 0);
            // Ties: the writing decides. All capitals read surname first, otherwise first name first; and the surname is one word when the line is
            // surname first and the last word when it is first name first.
            var preference = ((surnameFirst == allUpper) ? 2 : 0) + (surnameFirst ? (split == 1 ? 1 : 0) : (split == words.Count - 1 ? 1 : 0));
            if (best is null || score > best.Value.Score || (score == best.Value.Score && preference > best.Value.Preference))
                best = (score, preference, first, last);
        }

        for (var split = 1; split < words.Count; split++)
        {
            var head = words.Take(split).ToList();
            var tail = words.Skip(split).ToList();
            if (RealWords(head) == 0 || RealWords(tail) == 0)
                continue;

            Consider(JoinName(tail), JoinName(head), surnameFirst: true, split);
            Consider(JoinName(head), JoinName(tail), surnameFirst: false, split);
        }

        if (best is not { } chosen)
            return (null, null, 0);

        var confidence = chosen.Score switch { 2 => 95, 1 => 85, _ => words.Count == 2 ? 68 : 60 };
        return (chosen.First, chosen.Last, confidence);
    }

    // "residente in Milano (MI), Via Roma n. 1", "Residente in: MONZA (MB), ...", "con sede legale in ...".
    [GeneratedRegex(@"\b(?:residente|domiciliat[oa]|con\s+sede(?:\s+legale)?|sede\s+legale|residenza)\s*(?:in\b|a\b|presso\b)?\s*[:\-]?\s*(?:in\s+)?", Options, Timeout)]
    private static partial Regex ResidenceLabel();

    // Where a residence ends: the next thing the introduction says, or the next section of the contract.
    [GeneratedRegex(
        @",?\s*(?:\bc\.?\s?f\b|\bcod(?:ice|\.)?\s*fisc|\bp\.?\s?iva\b|\bpartita\s+iva\b|\bnat[oa]\b|\btelefono\b|\btel\b|\be-?mail\b|\bpec\b|\bdocumento\b|\bidentificat|\bmunit[oa]\b|\bdi\s+seguito\b|\bd'ora\b|\bin\s+qualit|\bcognome\b|\bcodice\b|\bcittadinanza\b|\bimmobile\b|\boggetto\b|\bpremess[aeoi]\b|\bsi\s+conviene\b|\bart(?:\.|icolo)|\b(?:locator[ei]|locatrice|conduttor[ei]|conduttrice)\b)|\(\s*(?:di\s+seguito|d'ora)|;",
        Options,
        Timeout)]
    private static partial Regex ResidenceEnd();

    // What a cut leaves behind at the end: ", e il" (before "il Conduttore"), ". Il" (before "Il Locatore").
    [GeneratedRegex(@"[\s,;.:\-]+(?:e|ed|il|la|lo)$", Options, Timeout)]
    private static partial Regex TrailingConnector();

    /// <summary>The text up to the first full stop that ends a sentence (not the one of an initial, "G. Garibaldi").</summary>
    private static string CutAtSentenceEnd(string text, out bool cut)
    {
        foreach (Match match in SentenceEnd().Matches(text))
        {
            var initial = match.Index > 0 && char.IsLetter(text[match.Index - 1]) && (match.Index < 2 || !char.IsLetter(text[match.Index - 2]));
            if (initial)
                continue;

            cut = true;
            return text[..match.Index];
        }

        cut = false;
        return text;
    }

    private static (string? Value, int Confidence) ReadResidence(string flat, int start, int end, int codeIndex)
    {
        if (start >= end)
            return (null, 0);

        var label = ResidenceLabel().Match(flat[start..end]);
        if (!label.Success)
            return (null, 0);

        var from = start + label.Index + label.Length;
        var limit = Math.Min(end, from + 150);
        var ended = false;
        // A fiscal code written after the residence ends it, wherever the label for it is missing.
        if (codeIndex > from && codeIndex < limit)
        {
            limit = codeIndex;
            ended = true;
        }

        var text = flat[from..limit];
        var stop = ResidenceEnd().Match(text);
        if (stop.Success)
        {
            text = text[..stop.Index];
            ended = true;
        }

        text = CutAtSentenceEnd(text, out var sentence);
        ended |= sentence;

        text = text.Trim(' ', ',', ';', '.', ':', '-', '"');
        for (var i = 0; i < 3; i++)
            text = TrailingConnector().Replace(text, string.Empty).Trim(' ', ',', ';', '.', ':', '-', '"');

        // An address is a few words: a residence that nothing ended, or that runs on, is the contract's next sentences and not an address.
        if (text.Length < 4 || text.Length > Math.Min(Party.ResidenceMaxLength, 120) || !text.Any(char.IsLetter))
            return (null, 0);

        return (text, ended ? 82 : 60);
    }

    [GeneratedRegex(@"\b(?:cittadinanza|cittadin[oa]|nazionalit[\u00e0a])\s+(?<c>\p{L}+)", Options, Timeout)]
    private static partial Regex Citizenship();

    private static (string? Value, int Confidence) ReadCitizenship(string flat, int start, int end)
    {
        if (start >= end)
            return (null, 0);

        foreach (Match match in Citizenship().Matches(flat[start..end]))
        {
            if (match.Groups["c"].Value.StartsWith("italian", StringComparison.OrdinalIgnoreCase))
                return ("IT", 90);
        }

        return (null, 0);
    }

    // ---- all together ----------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ReadParty> ReadParties(string flat)
    {
        var codes = ContractFiscalCodes.Find(flat);
        if (codes.Count == 0)
            return [];

        var intros = new List<Intro>(codes.Count);
        var previousEnd = 0;
        foreach (var code in codes)
        {
            intros.Add(FindIntro(flat, code, previousEnd));
            previousEnd = code.Index + code.Length;
        }

        // What each person's introduction covers: from where it starts to where the next one does, and a few lines after the code at most.
        var windowEnds = new int[intros.Count];
        for (var i = 0; i < intros.Count; i++)
        {
            var codeEnd = intros[i].Code.Index + intros[i].Code.Length;
            var next = i + 1 < intros.Count ? intros[i + 1].Start : flat.Length;
            windowEnds[i] = Math.Max(codeEnd, Math.Min(next, codeEnd + 260));
        }

        var roles = new RoleEvidence[intros.Count];
        for (var i = 0; i < intros.Count; i++)
            roles[i] = ResolveRole(flat, intros[i], windowEnds[i]);

        // "Mario Rossi (...) e Anna Rossi (...), di seguito congiuntamente Locatori": the first has no role of its own, the next says it for both.
        for (var i = 0; i + 1 < intros.Count; i++)
        {
            if (roles[i].Role is not null || roles[i + 1].Role is not { } shared || !roles[i + 1].Plural)
                continue;

            var codeEnd = intros[i].Code.Index + intros[i].Code.Length;
            var between = intros[i + 1].Start > codeEnd ? flat[codeEnd..intros[i + 1].Start] : string.Empty;
            if (between.Length <= 60 && !Regex.IsMatch(between, RolePattern, Options))
                roles[i] = new RoleEvidence(shared, 85, true);
        }

        var parties = new List<ReadParty>();
        for (var i = 0; i < intros.Count; i++)
        {
            if (roles[i].Role is not { } role || roles[i].Confidence < AcceptedConfidence)
                continue;

            var intro = intros[i];
            var (first, last, nameConfidence) = AssignName(intro.NameWords, intro.Code.Code);
            var (residence, residenceConfidence) = ReadResidence(flat, intro.Start, windowEnds[i], intro.Code.Index);
            var (citizenship, citizenshipConfidence) = ReadCitizenship(flat, intro.Start, windowEnds[i]);

            parties.Add(new ReadParty
            {
                Role = role,
                RoleConfidence = roles[i].Confidence,
                FiscalCode = intro.Code.Code,
                FiscalCodeRepaired = intro.Code.Repaired,
                FiscalCodeConfidence = intro.Code.Repaired ? 75 : 98,
                FirstName = first,
                LastName = last,
                NameConfidence = nameConfidence,
                Residence = residence,
                ResidenceConfidence = residenceConfidence,
                Citizenship = citizenship,
                CitizenshipConfidence = citizenshipConfidence,
            });
        }

        return parties;
    }
}
