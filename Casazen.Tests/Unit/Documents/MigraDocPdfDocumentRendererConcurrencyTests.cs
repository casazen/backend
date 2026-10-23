using System.Collections;
using System.Collections.Concurrent;
using Casazen.Core.Documents;
using Casazen.Infrastructure.Documents;
using Xunit;

namespace Casazen.Tests.Unit.Documents;

/// <summary>
/// BE-PDF-01: PDFsharp and MigraDoc share state between documents and are not safe when two documents are built at the
/// same time, so <see cref="MigraDocPdfDocumentRenderer"/> renders one document at a time in the whole process.
/// Without that, PDFs rendered together came out with words missing or garbled (found by QA-INFRA-01: with 16 threads
/// 13 to 60 of 192 PDFs, none with one thread). These tests use the real renderer: the first shows the symptom (the text
/// of every PDF rendered by 16 threads at once is the text of the same document rendered alone), the second the cause
/// (a render never starts inside the libraries while another one is there), which does not depend on how many cores
/// the machine has.
/// </summary>
public class MigraDocPdfDocumentRendererConcurrencyTests
{
    private const int ThreadCount = 16;
    private const int RendersPerThread = 4;

    /// <summary>How long a test waits for something that must happen; only reached when something is stuck.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>How long the first render of the overlap test stays inside the libraries, waiting for a second one to get in.</summary>
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Render_SixteenThreadsAtOnce_EveryPdfHasTheTextOfTheSerialRender()
    {
        var content = AllCharactersDocument();
        var expected = PdfTestReader.Text(new MigraDocPdfDocumentRenderer().Render(content));
        Assert.Equal(expected, PdfTestReader.Text(new MigraDocPdfDocumentRenderer().Render(content)));
        Assert.Contains("Pagina 1 di", expected, StringComparison.Ordinal);

        // Half of the threads share one renderer, as the singleton of the container is shared by the requests; the others
        // have their own, as tests and jobs do: the guarantee must hold between instances and not only within one.
        var shared = new MigraDocPdfDocumentRenderer();
        var pdfs = new byte[ThreadCount * RendersPerThread][];
        var errors = new ConcurrentQueue<Exception>();
        using var ready = new CountdownEvent(ThreadCount);
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, ThreadCount).Select(t => new Thread(() =>
        {
            var renderer = t % 2 == 0 ? shared : new MigraDocPdfDocumentRenderer();
            ready.Signal();
            go.Wait();
            for (var i = 0; i < RendersPerThread; i++)
            {
                try
                {
                    pdfs[(t * RendersPerThread) + i] = renderer.Render(content);
                }
                catch (Exception e)
                {
                    errors.Enqueue(e);
                }
            }
        })
        { IsBackground = true, Name = $"pdf-render-{t}" }).ToList();

        threads.ForEach(t => t.Start());
        Assert.True(ready.Wait(Patience), "the render threads did not start");
        go.Set();
        foreach (var thread in threads)
            Assert.True(thread.Join(Patience), $"{thread.Name} is stuck inside Render");

        if (!errors.IsEmpty)
            Assert.Fail($"{errors.Count} of {pdfs.Length} renders threw; first: {errors.First()}");

        var differences = pdfs.Select(PdfTestReader.Text).Where(text => text != expected).ToList();
        if (differences.Count > 0)
            Assert.Fail($"{differences.Count} of {pdfs.Length} PDFs rendered at the same time differ from the same document rendered alone; first: {Difference(expected, differences[0])}");
    }

    [Fact]
    public void Render_SecondRendererWhileOneIsRendering_WaitsUntilTheFirstHasFinished()
    {
        // MigraDoc builds the document by reading its blocks, inside the libraries: the first render is held there for a
        // moment while a second one, on another instance, is started. The second must not get in before the first is done.
        using var firstIsInside = new ManualResetEventSlim();
        using var secondIsInside = new ManualResetEventSlim();
        var overlapped = false;
        var first = ObservedDocument("Primo", () =>
        {
            firstIsInside.Set();
            overlapped = secondIsInside.Wait(OverlapWindow);
        });
        var second = ObservedDocument("Secondo", () => secondIsInside.Set());

        var errors = new ConcurrentQueue<Exception>();
        byte[]? firstPdf = null;
        byte[]? secondPdf = null;
        var firstThread = Start(() => firstPdf = new MigraDocPdfDocumentRenderer().Render(first), errors);
        Assert.True(firstIsInside.Wait(Patience), "the first render never got inside the libraries");
        var secondThread = Start(() => secondPdf = new MigraDocPdfDocumentRenderer().Render(second), errors);

        Assert.True(firstThread.Join(Patience), "the first render is stuck");
        Assert.True(secondThread.Join(Patience), "the second render is stuck");

        if (!errors.IsEmpty)
            Assert.Fail($"{errors.Count} of 2 renders threw; first: {errors.First()}");

        Assert.False(overlapped, "a second render got inside the libraries while the first was still there");
        Assert.Contains("Testo di Primo", PdfTestReader.Text(firstPdf!), StringComparison.Ordinal);
        Assert.Contains("Testo di Secondo", PdfTestReader.Text(secondPdf!), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every character the embedded font offers to CasaZen documents (Basic Latin, Latin-1 and Latin Extended-A, Greek,
    /// Cyrillic, typographic punctuation) in regular and bold paragraphs and in a table, with headings and a watermark.
    /// The more different glyphs a render looks up, the more often two overlapping renders hit the shared state of the
    /// libraries: of the shapes tried in BE-PDF-01 (numbered words, long words, tables, contracts) this was the one that
    /// corrupted the most PDFs.
    /// </summary>
    private static PdfDocumentContent AllCharactersDocument()
    {
        var characters = new List<char>();
        AddRange(characters, 0x21, 0x7E);
        AddRange(characters, 0xA1, 0x17F);
        AddRange(characters, 0x391, 0x3A1);
        AddRange(characters, 0x3A3, 0x3C9);
        AddRange(characters, 0x410, 0x44F);
        characters.AddRange("–—«»€‘’“”…•");

        // Five characters at a time, so that the text has words to break lines at and to compare.
        string Word(int start) => new(Enumerable.Range(start, 5).Select(k => characters[k % characters.Count]).ToArray());

        var blocks = new List<PdfBlock> { new PdfHeading("Tutti i caratteri del font") };
        for (var i = 0; i < 24; i++)
        {
            blocks.Add(new PdfParagraph(string.Join(' ', Enumerable.Range(0, 60).Select(w => Word((i * 7) + (w * 5)))), Bold: i % 3 == 0));
            if (i == 11)
                blocks.Add(new PdfHeading("Seconda parte", 3));
        }

        blocks.Add(new PdfTable(
            [new PdfTableColumn("Codice", 2), new PdfTableColumn("Descrizione", 4), new PdfTableColumn("Importo", 2, PdfCellAlignment.Right), new PdfTableColumn("Nota", 3, PdfCellAlignment.Center)],
            Enumerable.Range(0, 25)
                .Select(r => (IReadOnlyList<string>)[Word(r * 3), $"{Word(r * 5)} {Word(r * 11)}", $"{r * 17 % 1000},{r % 100:D2}", Word(r * 13)])
                .ToList()));

        return new PdfDocumentContent("Documento di prova", blocks) { Watermark = "BOZZA" };
    }

    private static void AddRange(List<char> characters, int from, int to)
    {
        for (var code = from; code <= to; code++)
            characters.Add((char)code);
    }

    /// <summary>A one-paragraph document whose list of blocks calls <paramref name="onRead"/> when the renderer reads it.</summary>
    private static PdfDocumentContent ObservedDocument(string title, Action onRead) =>
        new(title, new ObservedBlocks([new PdfParagraph($"Testo di {title}")], onRead));

    private static Thread Start(Action work, ConcurrentQueue<Exception> errors)
    {
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                errors.Enqueue(e);
            }
        })
        { IsBackground = true };
        thread.Start();
        return thread;
    }

    /// <summary>What is missing from and what is not expected in <paramref name="actual"/>, word by word, to read a failure at a glance.</summary>
    private static string Difference(string expected, string actual)
    {
        var expectedWords = expected.Split(' ');
        var actualWords = actual.Split(' ');
        var balance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in expectedWords)
            balance[word] = balance.GetValueOrDefault(word) + 1;
        foreach (var word in actualWords)
            balance[word] = balance.GetValueOrDefault(word) - 1;

        var missing = balance.Where(b => b.Value > 0).Select(b => b.Key).Take(3);
        var unexpected = balance.Where(b => b.Value < 0).Select(b => b.Key).Take(3);
        return $"{actualWords.Length} words instead of {expectedWords.Length}, missing [{string.Join(", ", missing)}], not expected [{string.Join(", ", unexpected)}]";
    }

    private sealed class ObservedBlocks(IReadOnlyList<PdfBlock> blocks, Action onRead) : IReadOnlyList<PdfBlock>
    {
        public int Count => blocks.Count;

        public PdfBlock this[int index] => blocks[index];

        public IEnumerator<PdfBlock> GetEnumerator()
        {
            onRead();
            return blocks.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
