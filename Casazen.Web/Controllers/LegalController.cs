using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Legal;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public legal documents (PL-14): version, date in force and the text provided by the product owner (D14), read by
/// the web pages <c>/legale/*</c> and the onboarding consents step. <c>lang</c> (<c>it</c> or <c>en</c>) picks the
/// text language, otherwise the request culture; Italian when no translation exists.
/// </summary>
[ApiController]
[Route("api/legal")]
[AllowAnonymous]
public class LegalController(ILegalDocumentService legalDocumentService) : ControllerBase
{
    [HttpGet("subprocessors")]
    [ProducesResponseType(typeof(SubprocessorsDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<SubprocessorsDocumentDto> GetSubprocessors() =>
        Ok(legalDocumentService.GetSubprocessors().ToDto());

    [HttpGet("dpa")]
    [ProducesResponseType(typeof(LegalDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<LegalDocumentDto> GetDpa([FromQuery] string? lang) => Document(LegalDocumentKind.Dpa, lang);

    [HttpGet("tos")]
    [ProducesResponseType(typeof(LegalDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<LegalDocumentDto> GetTos([FromQuery] string? lang) => Document(LegalDocumentKind.Tos, lang);

    [HttpGet("privacy")]
    [ProducesResponseType(typeof(LegalDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<LegalDocumentDto> GetPrivacy([FromQuery] string? lang) =>
        Document(LegalDocumentKind.Privacy, lang);

    private ActionResult<LegalDocumentDto> Document(LegalDocumentKind kind, string? lang) =>
        Ok(legalDocumentService.Get(kind).ToDto(kind, legalDocumentService.GetText(kind, lang)));
}
