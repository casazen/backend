using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalDocumentsController(AppDbContext db, IFiscalDocumentService documents)
    : GuestPortalControllerBase
{
    [HttpGet("documents")]
    public async Task<ActionResult<IReadOnlyList<DocumentSummary>>> Get(CancellationToken ct) =>
        await ForStay().OrderByDescending(d => d.IssueDate)
            .Select(d => new DocumentSummary(d.Id, d.Kind, d.Number!, d.IssueDate!.Value, d.Total, d.Currency))
            .ToListAsync(ct);

    [HttpGet("documents/{documentId:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid documentId, CancellationToken ct)
    {
        if (!await ForStay().AnyAsync(d => d.Id == documentId, ct))
            return LinkInvalid();

        var pdf = await documents.GetPdfAsync(documentId, Portal.Language, ct);
        return File(pdf, "application/pdf", $"documento-{documentId:D}.pdf");
    }

    private IQueryable<FiscalDocument> ForStay()
    {
        var portal = Portal;
        return db.FiscalDocuments.AsNoTracking()
            .Where(d => d.BookingId == portal.BookingId && d.OrgId == portal.OrgId
                && d.Purpose == FiscalDocumentPurpose.GuestStay && d.Source == FiscalDocumentSource.CasaZen
                && d.Status == FiscalDocumentStatus.Issued && d.Number != null && d.IssueDate != null);
    }

    public sealed record DocumentSummary(Guid Id, FiscalDocumentKind Kind, string Number, DateOnly IssueDate, decimal Total, string Currency);
}
