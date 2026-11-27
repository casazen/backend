using Casazen.Core.Documents;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The public page of the document of a stay that the guest got by e-mail (E12/F01/US02/T01, NF-83): the page of the link (<c>/documents/view/{id}?token=</c>) reads the summary and the PDF of the
/// document from here. <c>POST api/public/fiscal-documents/{id}</c> and <c>POST api/public/fiscal-documents/{id}/pdf</c>, anonymous, with the token of the link in the <b>body</b>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One answer.</b> A missing, wrong, expired or foreign token (made for another document or another purpose), a document that does not exist and one that is not an issued guest document of
/// CasaZen are all the same 404 <c>document_link_invalid</c>: nothing says which part was wrong, and no document data is in it.</item>
/// <item><b>The token is never logged.</b> It is in the body, never in the address, and nothing here or in the service writes it to a log; the answers are never indexable and never cached
/// (<see cref="PrivateAnswerAttribute"/>).</item>
/// <item><b>Rate limited per client IP</b> (<see cref="RateLimitPolicies.PublicFiscalDocumentDownload"/>) and bounded in size.</item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/public/fiscal-documents")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicFiscalDocumentDownload)]
[PrivateAnswer]
public class PublicFiscalDocumentsController(IFiscalDocumentDeliveryService delivery) : ControllerBase
{
    /// <summary>The largest body taken: the token of a link is a few hundred characters.</summary>
    public const int MaxBodyBytes = 4096;

    /// <summary>
    /// The summary of the document: the kind, the number, the day of the issue, the total and its currency. 404 <c>document_link_invalid</c> for every token that does not open this document.
    /// </summary>
    [HttpPost("{id:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(PublicFiscalDocumentSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Summary(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PublicFiscalDocumentLinkRequest? request = null)
    {
        var summary = await delivery.GetSummaryByLinkAsync(id, request?.Token, HttpContext.RequestAborted);
        return summary is null ? LinkInvalid() : Ok(PublicFiscalDocumentSummaryDto.From(summary));
    }

    /// <summary>
    /// The PDF of the document, in the language of the request, as the host sees it. 404 <c>document_link_invalid</c> for every token that does not open this document.
    /// </summary>
    [HttpPost("{id:guid}/pdf")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pdf(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PublicFiscalDocumentLinkRequest? request = null)
    {
        var file = await delivery.GetPdfByLinkAsync(id, request?.Token, DocumentLanguages.Current, HttpContext.RequestAborted);
        return file is null ? LinkInvalid() : File(file.Content, "application/pdf", file.FileName);
    }

    private ObjectResult LinkInvalid() =>
        this.ApiProblem(StatusCodes.Status404NotFound, FiscalDocumentDeliveryErrors.LinkInvalid, "DocumentLinkInvalid");
}
