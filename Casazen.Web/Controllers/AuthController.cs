using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(ILogger<AuthController> logger) : ControllerBase
{
    [HttpGet("profile")]
    [Authorize]
    public IActionResult GetProfile()
    {
        var userId = User.FindFirst("sub")?.Value;
        var email = User.FindFirst("email")?.Value;
        var name = User.FindFirst("name")?.Value;

        return Ok(new
        {
            userId,
            email,
            name,
            isAuthenticated = User.Identity?.IsAuthenticated ?? false
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        logger.LogInformation("User {UserId} logged out", User.FindFirst("sub")?.Value);
        return Ok(new { message = "Logged out successfully" });
    }
}