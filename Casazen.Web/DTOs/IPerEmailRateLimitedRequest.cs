namespace Casazen.Web.DTOs;

public interface IPerEmailRateLimitedRequest
{
    string? Email { get; }

    string? RateLimitScope { get; }
}
