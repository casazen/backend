using Casazen.Core.Documents;
using Casazen.Infrastructure.Documents;

namespace Casazen.Tests.Unit.Documents;

/// <summary>
/// The application's PDF renderer behind a lock shared by the whole test process (QA-INFRA-01). PDFsharp/MigraDoc is not safe
/// when two documents are rendered at the same time in one process: the renderer has no lock, and a probe that rendered the
/// same 3,200-word document from 16 threads got 60 of 192 PDFs with words missing (3,194 to 3,198 of 3,200 found). Tests run
/// in parallel, so two of them rendering at once corrupted each other's text, and the assertions on the extracted words
/// (<c>GeneratePdfAsync_ApprovedTemplateOver20000Characters…</c>, <c>FiscalReportsTests</c>…) failed now and then, mostly on a
/// loaded machine. Behind this gate the tests render one document at a time. The renderer itself is unchanged: serializing
/// <c>MigraDocPdfDocumentRenderer.Render</c> in production is a separate task, and this wrapper can go when it lands.
/// </summary>
internal sealed class SerializedPdfRenderer(IPdfDocumentRenderer renderer) : IPdfDocumentRenderer
{
    private static readonly Lock Gate = new();

    public SerializedPdfRenderer()
        : this(new MigraDocPdfDocumentRenderer())
    {
    }

    public byte[] Render(PdfDocumentContent content)
    {
        lock (Gate)
            return renderer.Render(content);
    }
}
