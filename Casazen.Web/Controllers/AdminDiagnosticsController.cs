using Casazen.Web.DTOs.Admin;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/diagnostics")]
[Authorize(Policy = "AdminOnly")]
public class AdminDiagnosticsController(IOptions<ForwardedHeadersOptions> forwardedHeadersOptions) : ControllerBase
{
    [HttpGet("client-ip")]
    [ProducesResponseType(typeof(ClientIpDiagnosticsDto), StatusCodes.Status200OK)]
    public ActionResult<ClientIpDiagnosticsDto> GetClientIp()
    {
        var options = forwardedHeadersOptions.Value;
        var originalPeer = Request.Headers[ForwardedHeadersDefaults.XOriginalForHeaderName].ToString();

        return Ok(new ClientIpDiagnosticsDto
        {
            ClientIp = ClientIp.GetString(HttpContext),
            RateLimitKey = ClientIp.GetRateLimitKey(HttpContext),
            OriginalPeer = string.IsNullOrEmpty(originalPeer) ? null : originalPeer,
            UnprocessedForwardedFor = Request.Headers[ForwardedHeadersDefaults.XForwardedForHeaderName]
                .SelectMany(value => (value ?? string.Empty).Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList(),
            Scheme = Request.Scheme,
            ForwardLimit = options.ForwardLimit,
            KnownNetworks = options.KnownIPNetworks.Select(network => network.ToString()).ToList(),
            KnownProxies = options.KnownProxies.Select(proxy => proxy.ToString()).ToList(),
        });
    }
}
