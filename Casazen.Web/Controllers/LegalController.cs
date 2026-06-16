using Casazen.Core.Services;
using Casazen.Web.DTOs.Legal;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

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
    public ActionResult<LegalDocumentDto> GetDpa() =>
        Ok(legalDocumentService.GetDpa().ToDto());

    [HttpGet("tos")]
    [ProducesResponseType(typeof(LegalDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<LegalDocumentDto> GetTos() =>
        Ok(legalDocumentService.GetTos().ToDto());

    [HttpGet("privacy")]
    [ProducesResponseType(typeof(LegalDocumentDto), StatusCodes.Status200OK)]
    public ActionResult<LegalDocumentDto> GetPrivacy() =>
        Ok(legalDocumentService.GetPrivacy().ToDto());
}
