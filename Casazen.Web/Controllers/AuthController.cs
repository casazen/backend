using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    IUserService userService,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var user = await userService.RegisterUserAsync(
                request.Email,
                request.FirstName,
                request.LastName,
                request.Password);

            logger.LogInformation("User registered successfully: {Email}", user.Email);

            return Ok(new RegisterResponse
            {
                UserId = user.Id,
                Email = user.Email,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Message = "User registered successfully. Note: In production, this would create user in Auth0."
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Registration failed: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Registration validation failed: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

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

public record RegisterRequest(
    string Email,
    string FirstName,
    string LastName,
    string Password);

public record RegisterResponse
{
    public string UserId { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}