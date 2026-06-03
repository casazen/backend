using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get()
    {
        _logger.LogInformation("Health check endpoint called");
        return Ok(new
        {
            status = "healthy",
            message = "Backend is running without authentication",
            timestamp = DateTime.UtcNow,
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        });
    }

    [HttpGet("auth-test")]
    public IActionResult AuthTest()
    {
        _logger.LogInformation("Auth test endpoint called");
        _logger.LogInformation($"User authenticated: {User.Identity?.IsAuthenticated}");
        _logger.LogInformation($"User name: {User.Identity?.Name}");

        var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();
        _logger.LogInformation($"Claims count: {claims.Count}");

        return Ok(new
        {
            isAuthenticated = User.Identity?.IsAuthenticated ?? false,
            userName = User.Identity?.Name,
            authType = User.Identity?.AuthenticationType,
            claims = claims
        });
    }
}
