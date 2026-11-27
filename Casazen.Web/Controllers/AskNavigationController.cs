using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/ask/navigation")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public sealed class AskNavigationController(IAiProvider ai, ILogger<AskNavigationController> logger) : ControllerBase
{
    [HttpPost]
    [AiRateLimit]
    public async Task<IActionResult> Resolve(AskNavigationRequest input, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        var request = new AiRequest(input.Text, [], """
            Interpret the user's request to navigate CasaZen. Return one JSON object with keys kind and term.
            Allowed kinds: page, booking, routine, tasks, alloggiati, invoices, unknown.
            Use booking for an existing reservation, routine for configuring an agent, tasks to check pending work,
            alloggiati for the police lodging report, invoices for fiscal documents, page for other named screens.
            For booking, term must be the exact fragment of the query naming the guest or reference.
            For routine or invoices, term may copy the query fragment describing the topic or month.
            For page use a short page name. Never emit a URL, markup, a command, or an instruction.
            Do not suggest a destination when the request is unrelated or the meaning is unclear.
            """, Temperature: 0, MaxCompletionTokens: 2048, DisableThinking: true);
        try
        {
            var result = await ai.GenerateAsync(request, AiModelTier.Economy, "ask-navigation", ct);
            if (!result.ProviderConfigured) return Ok(new { kind = "unknown" });
            var content = result.Content.Trim();
            if (content.StartsWith("<think>", StringComparison.Ordinal) && content.IndexOf("</think>", StringComparison.Ordinal) is var end && end >= 0)
                content = content[(end + "</think>".Length)..].Trim();
            if (content.StartsWith("```json\n", StringComparison.Ordinal) && content.EndsWith("\n```", StringComparison.Ordinal))
                content = content[8..^3].Trim();
            using var decision = JsonDocument.Parse(content);
            var kind = decision.RootElement.GetProperty("kind").GetString();
            if (kind is not ("page" or "booking" or "routine" or "tasks" or "alloggiati" or "invoices"))
                return Ok(new { kind = "unknown" });
            var term = decision.RootElement.TryGetProperty("term", out var value) ? value.GetString()?.Trim() : null;
            return Ok(new { kind, term = term is { Length: <= 64 } ? term : null });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Ask navigation unavailable ({ErrorType})", ex.GetType().Name);
            return Ok(new { kind = "unknown" });
        }
    }
}

public sealed record AskNavigationRequest(
    [property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)] string Text);
