using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/agent-playground")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminAgentPlaygroundController(PresetAgentTrainer trainer, IConfiguration configuration) : ControllerBase
{
    private bool CanTrain => User.GetUserId() is { Length: > 0 } subject &&
        string.Equals(configuration["Ai:Playground:AdminSubject"], subject, StringComparison.Ordinal);

    [HttpGet("access")]
    public IActionResult Access() => Ok(new { allowed = CanTrain });

    [HttpGet("{key}")]
    public async Task<ActionResult<AgentTrainingState>> Get(string key, CancellationToken ct) =>
        CanTrain ? Ok(await trainer.GetAsync(key, ct)) : Forbid();

    [HttpPost("{key}/train")]
    [AiRateLimit]
    [RequestSizeLimit(24_000)]
    public async Task<ActionResult<AgentTrainingState>> Train(string key, TrainAgentRequest request, CancellationToken ct) =>
        CanTrain ? Ok(await trainer.TrainAsync(key, request.Draft, request.Revision, ct)) : Forbid();

    [HttpPut("{key}/active")]
    public async Task<ActionResult<AgentTrainingState>> Activate(string key, ActivateAgentRequest request, CancellationToken ct) =>
        CanTrain ? Ok(await trainer.ActivateAsync(key, request.Revision, request.Restore, ct)) : Forbid();
}

public sealed record TrainAgentRequest(int Revision, AgentTrainingDraft Draft);
public sealed record ActivateAgentRequest(int Revision, bool Restore);
