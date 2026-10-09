using Casazen.Core.Documents;
using Xunit;

namespace Casazen.Tests.Unit.Documents;

/// <summary>
/// QA-INFRA-01: the gate of <see cref="SerializedPdfRenderer"/> is what keeps the PDF tests deterministic, so it is checked
/// without PDFs: no matter how many threads or wrapper instances, the renderer behind it is never inside two calls at once.
/// </summary>
public class SerializedPdfRendererTests
{
    private static readonly PdfDocumentContent Content = new("Titolo", [new PdfParagraph("Testo")]);

    [Fact]
    public async Task Render_CallsFromManyThreadsAndInstances_NeverRunTheRendererTwiceAtOnce()
    {
        var renderer = new OverlapDetectingRenderer();
        var instances = new[] { new SerializedPdfRenderer(renderer), new SerializedPdfRenderer(renderer) };

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => instances[i % 2].Render(Content))));

        Assert.Equal(12, renderer.Calls);
        Assert.Equal(1, renderer.MaxConcurrentCalls);
    }

    [Fact]
    public void Render_DefaultInstance_ProducesAPdf()
    {
        var pdf = new SerializedPdfRenderer().Render(Content);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    private sealed class OverlapDetectingRenderer : IPdfDocumentRenderer
    {
        private int _inside;
        private int _calls;
        private int _maxConcurrent;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrent);

        public byte[] Render(PdfDocumentContent content)
        {
            var now = Interlocked.Increment(ref _inside);
            int seen;
            while ((seen = Volatile.Read(ref _maxConcurrent)) < now && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
            {
            }

            Thread.Sleep(15); // long enough for another thread to arrive while this one is inside
            Interlocked.Increment(ref _calls);
            Interlocked.Decrement(ref _inside);
            return [];
        }
    }
}
