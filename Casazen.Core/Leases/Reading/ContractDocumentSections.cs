namespace Casazen.Core.Leases.Reading;

/// <summary>The three kinds of section a document that holds the contract and what goes with it (CF-DOCS-REAL) is made of.</summary>
public enum DocumentSectionKind
{
    /// <summary>The contract itself, as it was signed.</summary>
    Contract = 0,

    /// <summary>The attestation of conformity ("Attestazione di Rispondenza") the associations release for a canone concordato.</summary>
    Attestation = 1,

    /// <summary>The sheet that works out the rent from the territorial agreement ("Scheda Riepilogativa Canone").</summary>
    CalculationSheet = 2,
}

/// <summary>A run of pages of one kind: <see cref="FirstPage"/> and <see cref="LastPage"/> count from 1 and are both part of it.</summary>
public sealed record DocumentSection(DocumentSectionKind Kind, int FirstPage, int LastPage);

/// <summary>
/// What <see cref="ContractDocumentSections"/> made of a document: its sections, the pages that hold no readable text and whether the
/// sections came in a strange order. Holds page numbers and nothing of the text: it can be kept with the document.
/// </summary>
public sealed record RecognizedSections(IReadOnlyList<DocumentSection> Sections, IReadOnlyList<int> UnreadablePages, bool OutOfOrder)
{
    public bool Contains(DocumentSectionKind kind) => Sections.Any(section => section.Kind == kind);

    public bool ContainsAttestation => Contains(DocumentSectionKind.Attestation);

    public bool ContainsCalculationSheet => Contains(DocumentSectionKind.CalculationSheet);
}

/// <summary>The stable names the sections have in what is kept with a document and in what the API says.</summary>
public static class DocumentSectionKinds
{
    public const string Contract = "contract";
    public const string Attestation = "attestation";
    public const string CalculationSheet = "calculationSheet";

    public static string Code(DocumentSectionKind kind) => kind switch
    {
        DocumentSectionKind.Attestation => Attestation,
        DocumentSectionKind.CalculationSheet => CalculationSheet,
        _ => Contract,
    };
}

/// <summary>
/// Tells the contract, the attestation and the calculation sheet apart in the pages of one document, from their text (CF-DOCS-REAL): the
/// file that the guided service delivers holds the signed contract, the attestation of conformity and the sheet of the rent one after the
/// other, as scans, and each must be read by its own reader (the numbers of the attestation would confuse the reader of the contract).
/// </summary>
/// <remarks>
/// <para>Each page is given a score for each kind. A title at the top of the page weighs most ("Attestazione di Rispondenza", "Scheda
/// Riepilogativa Canone", "Contratto di Locazione…"), looked for with the small mistakes of an OCR in mind (a few letters wrong, a word
/// split or glued); phrases of the body weigh less ("articolo 3", "punteggio totale", "riepilogo degli importi"). The pages are then given
/// the sequence of kinds of the best total, where changing kind costs something: a page without a sign of its own stays with the section
/// it follows, one wrong word does not split a section, and a page whose title the OCR could not read is still put right by its body.
/// A document that shows no sign of the attestation or of the sheet is one contract: no section found is not an error.</para>
/// <para>Pure function of the text: no I/O, nothing logged, and nothing of the text in the answer.</para>
/// </remarks>
public static class ContractDocumentSections
{
    /// <summary>A page with fewer letters and digits than this has nothing readable on it.</summary>
    private const int UnreadableBelow = 25;

    /// <summary>The part of the top of a page, in letters, where a title may stand (a stray mark or a stamp may come before it).</summary>
    private const int TitleZone = 240;

    private const int TitleAtTheTop = 80;
    private const double TitleScore = 10;
    private const double TitleLowerScore = 6;
    private const double BodyCap = 8;
    private const double SwitchPenalty = 5;

    /// <summary>A page that shows nothing goes to the contract: the person said it is one.</summary>
    private const double ContractBias = 0.5;

    private const int BodyReach = 6000;

    private static readonly string[][] Titles =
    [
        ["contrattodilocazione", "locazioneabitativa", "contrattotipolocale", "contrattodiaffitto"],
        ["attestazionedirispondenza", "attestazionedeicontenutieconomici", "attestazionediconformita", "attestazionebilaterale"],
        ["schedariepilogativa", "schedadicalcolo", "schedacalcolodelcanone", "schedadeterminazionedelcanone"],
    ];

    private static readonly (string Words, int Weight)[][] Body =
    [
        [
            ("concede in locazione", 3), ("concedono in locazione", 3), ("denominato conduttore", 2), ("denominata locatrice", 2),
            ("denominato locatore", 2), ("letto approvato e sottoscritto", 3), ("letto confermato e sottoscritto", 3),
            ("a mente degli articoli", 3), ("le parti specificamente approvano", 3), ("e regolata dalle pattuizioni seguenti", 3),
        ],
        [
            ("attestano", 4), ("punteggio totale", 4), ("elementi oggettivi", 3), ("rilasciano congiuntamente", 4),
            ("sollevano espressamente", 3), ("dichiarazione delle parti", 3), ("organizzazione sindacale dei conduttori", 2),
            ("elemento caratteristica", 2), ("e pertanto collocata nella sub fascia", 3), ("attestazione di rispondenza", 2),
        ],
        [
            ("scheda riepilogativa", 4), ("riepilogo degli importi", 4), ("accordo e parametri", 4), ("canone massimo teorico", 3),
            ("procedimento applicato", 3), ("determinazione del canone", 2), ("le parti dichiarano che i valori", 3),
            ("canone convenuto al metro quadrato", 2), ("coefficiente dei correttivi", 2), ("e pertanto conforme", 2),
        ],
    ];

    /// <summary>The sections of a document, from the text of each of its pages (the first page is page 1).</summary>
    public static RecognizedSections Recognize(IReadOnlyList<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count == 0)
            return new RecognizedSections([], [], false);

        var unreadable = new List<int>();
        var emission = new double[pages.Count, 3];
        for (var p = 0; p < pages.Count; p++)
        {
            if (ReadingText.Letters(pages[p]) < UnreadableBelow)
                unreadable.Add(p + 1);

            Score(pages[p], emission, p);
        }

        var labels = Best(emission, pages.Count);
        return new RecognizedSections(Runs(labels), unreadable, IsOutOfOrder(labels));
    }

    /// <summary>The text of the pages of one kind, in the order they come, page after page.</summary>
    public static string TextOf(IReadOnlyList<string> pages, RecognizedSections sections, DocumentSectionKind kind)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(sections);

        var parts = new List<string>();
        foreach (var section in sections.Sections.Where(s => s.Kind == kind))
        {
            for (var page = section.FirstPage; page <= section.LastPage && page <= pages.Count; page++)
                parts.Add(pages[page - 1]);
        }

        return string.Join("\n", parts);
    }

    private static void Score(string page, double[,] emission, int index)
    {
        var normalized = ReadingText.Normalize(page);
        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        var zone = compact.Length > TitleZone ? compact[..TitleZone] : compact;
        var reach = compact.Length > BodyReach ? compact[..BodyReach] : compact;

        // The title that stands first at the top of the page is the page's; one that comes after it is a mention.
        var titleKind = -1;
        var titleEnd = int.MaxValue;
        for (var kind = 0; kind < Titles.Length; kind++)
        {
            foreach (var phrase in Titles[kind])
            {
                var (found, end) = ReadingText.FindLike(zone, phrase);
                if (found && end < titleEnd)
                {
                    titleKind = kind;
                    titleEnd = end;
                }
            }
        }

        for (var kind = 0; kind < Body.Length; kind++)
        {
            double score = 0;
            foreach (var (words, weight) in Body[kind])
            {
                var phrase = words.Replace(" ", string.Empty, StringComparison.Ordinal);
                var present = phrase.Length >= 12
                    ? ReadingText.FindLike(reach, phrase).Found
                    : ReadingText.HasWords(normalized, words);
                if (present)
                    score += weight;
            }

            if (kind == (int)DocumentSectionKind.Contract)
                score += ArticleHeadings(normalized);

            emission[index, kind] = Math.Min(score, BodyCap);
        }

        if (titleKind >= 0)
            emission[index, titleKind] += titleEnd <= TitleAtTheTop ? TitleScore : TitleLowerScore;

        emission[index, (int)DocumentSectionKind.Contract] += ContractBias;
    }

    /// <summary>"Articolo 1 (Durata)", "Articolo 2 (Canone)": the numbered articles of a contract (an attestation says "art.").</summary>
    private static double ArticleHeadings(string normalized)
    {
        var count = 0;
        var from = 0;
        while (from < normalized.Length)
        {
            var at = normalized.IndexOf("articolo ", from, StringComparison.Ordinal);
            if (at < 0)
                break;

            var digit = at + "articolo ".Length;
            if ((at == 0 || normalized[at - 1] == ' ') && digit < normalized.Length && char.IsAsciiDigit(normalized[digit]))
                count++;

            from = digit;
        }

        return count switch { 0 => 0, 1 => 3, _ => 5 };
    }

    /// <summary>The kind of each page that makes the total score highest, with a cost for every change of kind (the Viterbi path).</summary>
    private static int[] Best(double[,] emission, int pages)
    {
        var total = new double[pages, 3];
        var from = new int[pages, 3];
        for (var kind = 0; kind < 3; kind++)
            total[0, kind] = emission[0, kind];

        for (var p = 1; p < pages; p++)
        {
            for (var kind = 0; kind < 3; kind++)
            {
                var bestBefore = total[p - 1, kind];
                var bestKind = kind;
                for (var other = 0; other < 3; other++)
                {
                    var moved = total[p - 1, other] - SwitchPenalty;
                    if (other != kind && moved > bestBefore)
                    {
                        bestBefore = moved;
                        bestKind = other;
                    }
                }

                total[p, kind] = bestBefore + emission[p, kind];
                from[p, kind] = bestKind;
            }
        }

        var labels = new int[pages];
        var last = 0;
        for (var kind = 1; kind < 3; kind++)
        {
            if (total[pages - 1, kind] > total[pages - 1, last])
                last = kind;
        }

        labels[pages - 1] = last;
        for (var p = pages - 1; p > 0; p--)
            labels[p - 1] = from[p, labels[p]];

        return labels;
    }

    private static List<DocumentSection> Runs(int[] labels)
    {
        var runs = new List<DocumentSection>();
        var start = 0;
        for (var p = 1; p <= labels.Length; p++)
        {
            if (p < labels.Length && labels[p] == labels[start])
                continue;

            runs.Add(new DocumentSection((DocumentSectionKind)labels[start], start + 1, p));
            start = p;
        }

        return runs;
    }

    /// <summary>The kinds come contract, attestation, sheet; one that comes again after another, or before one that should precede it, is out of order.</summary>
    private static bool IsOutOfOrder(int[] labels)
    {
        var highest = -1;
        var seen = new HashSet<int>();
        for (var p = 0; p < labels.Length; p++)
        {
            if (p > 0 && labels[p] == labels[p - 1])
                continue;

            if (labels[p] < highest || !seen.Add(labels[p]))
                return true;

            highest = labels[p];
        }

        return false;
    }
}
