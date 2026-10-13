namespace Casazen.Core.Documents;

/// <summary>
/// The single PDF renderer of the application (LT-09, A7-14): contracts, RLI and IMU prefill, fiscal reports.
/// A4 pages with margins, automatic line wrapping and pagination, "Pagina N di M" footer, simple tables, fonts
/// embedded from the application (full Latin, Greek and Cyrillic coverage) so the output never depends on the fonts of
/// the host. Runbook: <c>docs/runbooks/lease-contract-templates.md</c>, section "PDF".
/// </summary>
public interface IPdfDocumentRenderer
{
    /// <summary>The PDF bytes of <paramref name="content"/>.</summary>
    byte[] Render(PdfDocumentContent content);
}
