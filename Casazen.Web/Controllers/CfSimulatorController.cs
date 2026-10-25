using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Dtos.CfSimulator;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The simulated module and status API of ConcordatoFacile (CF-SIM), for the test environment only. ConcordatoFacile has no environment of
/// its own to try the whole round with, so the web app plays its module (the page <c>/guided-form</c>, which reads and writes through
/// these endpoints) and the status client reads the practices it takes in (<c>docs/runbooks/concordatofacile-simulator.md</c>).
/// </summary>
/// <remarks>
/// Every action answers 404 like a route that does not exist unless <c>ConcordatoFacile:FormMode</c> is <c>Simulated</c>
/// (<see cref="CfSimulatorOnlyAttribute"/>), and the startup refuses that mode in Production: with the real service nothing here can be
/// reached. The endpoints are anonymous, as the real module is, and guarded the way it is: a practice is bound to a contract only by the
/// signed link (HMAC-SHA256 of the lease and the expiry with the link secret), and its state and its documents answer only to the
/// control token that the creation of the practice returned once. Rate limited per IP, bodies of a few hundred bytes.
/// </remarks>
[ApiController]
[Route("api/cf-simulator")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
[CfSimulatorOnly]
public class CfSimulatorController(ICfSimulatorService simulator) : ControllerBase
{
    private const int MaxBodyBytes = 4 * 1024;

    /// <summary>
    /// What the module learns when its page opens with the link: whether the signature and the expiry are good, the municipality the link
    /// names and, for a good link, the practices the contract already has. A link that is not good answers 200 with <c>valid: false</c>:
    /// the module can be filled in all the same (specification v0.2 §1).
    /// </summary>
    [HttpGet("link")]
    [ProducesResponseType(typeof(CfSimulatorLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CfSimulatorLinkResponse>> CheckLink([FromQuery] CfSimulatorLinkQuery query, CancellationToken cancellationToken)
    {
        var result = await simulator.CheckLinkAsync(query.ToParameters(), cancellationToken);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(new CfSimulatorLinkResponse
        {
            Valid = result.Valid,
            Reason = result.Reason,
            LeaseId = result.LeaseId,
            Comune = result.Comune,
            Provincia = result.Provincia,
            Practices = result.Practices.Select(CfSimulatorPracticeResponse.From).ToList(),
        });
    }

    /// <summary>
    /// The "send" of the module: takes in a practice (state <c>Received</c>) with the parameters of the link in the query string. Bound to
    /// the contract only when the link is good; another practice of that contract that is not over is closed as a duplicate. The answer
    /// carries the control token, which is not shown again.
    /// </summary>
    [HttpPost("practices")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(CfSimulatorCreatedPracticeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CfSimulatorCreatedPracticeResponse>> CreatePractice(
        [FromQuery] CfSimulatorLinkQuery query,
        CancellationToken cancellationToken)
    {
        var created = await simulator.CreatePracticeAsync(query.ToParameters(), cancellationToken);

        Response.Headers.CacheControl = "private, no-store";
        return StatusCode(StatusCodes.Status201Created, new CfSimulatorCreatedPracticeResponse
        {
            PracticeId = created.Practice.PracticeId,
            ControlToken = created.ControlToken,
            Status = created.Practice.Status,
            StatusChangedAt = created.Practice.StatusChangedAt,
            Linked = created.Linked,
        });
    }

    /// <summary>
    /// The test panel: sets the state of a practice, any state, and the reason when it is closed without attestation. 404 when the practice
    /// does not exist or the token is not its own (the two are not told apart); the state <c>Unknown</c> is not one of the service.
    /// </summary>
    [HttpPost("practices/{practiceId}/status")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(CfSimulatorPracticeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CfSimulatorPracticeResponse>> SetStatus(
        string practiceId,
        [FromBody] CfSimulatorSetStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Status is not { } status || status == CfPracticeStatus.Unknown)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "BadRequestDetail");

        var practice = await simulator.SetStatusAsync(practiceId, request.ControlToken, status, request.CloseReason, cancellationToken);
        if (practice is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail");

        Response.Headers.CacheControl = "private, no-store";
        return Ok(CfSimulatorPracticeResponse.From(practice));
    }

    /// <summary>
    /// A sample PDF of the practice, as ConcordatoFacile e-mails it: <c>contract</c>, <c>attestation</c> or <c>receipt</c>. The control token
    /// travels in the query string so that the panel can use a plain link; it is the token of a practice of a test environment, not a credential.
    /// </summary>
    [HttpGet("practices/{practiceId}/documents/{kind}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument(string practiceId, string kind, [FromQuery] string? controlToken, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var documentKind))
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail");

        var document = await simulator.RenderDocumentAsync(practiceId, controlToken, documentKind, cancellationToken);
        if (document is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail");

        Response.Headers.CacheControl = "private, no-store";
        return File(document.Content, "application/pdf", document.FileName);
    }

    private static bool TryParseKind(string kind, out CfSimulatorDocumentKind documentKind)
    {
        switch (kind.ToLowerInvariant())
        {
            case "contract":
                documentKind = CfSimulatorDocumentKind.Contract;
                return true;
            case "attestation":
                documentKind = CfSimulatorDocumentKind.Attestation;
                return true;
            case "receipt":
                documentKind = CfSimulatorDocumentKind.Receipt;
                return true;
            default:
                documentKind = default;
                return false;
        }
    }
}
