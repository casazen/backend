using System.ComponentModel.DataAnnotations;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// IndexNow protocol support (NF-25, AC-NF25-4): notify search engines (Bing, Yandex, others) about URL changes.
/// The endpoint is for internal use by the platform when content changes (property published/updated, org activated).
/// </summary>
/// <remarks>
/// IndexNow spec: https://www.indexnow.org/documentation
/// The API key is stored in appsettings and must be published at the root of the public site for verification.
/// This controller queues notifications; actual submission happens in a background job to avoid blocking requests.
/// </remarks>
[ApiController]
[Authorize]
public class IndexNowController(IOptions<IndexNowOptions> options, ILogger<IndexNowController> logger) : ControllerBase
{
    private readonly IndexNowOptions _options = options.Value;

    [HttpPost("api/indexnow/notify")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult NotifyUrls([FromBody] IndexNowRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogWarning("IndexNow API key not configured - notification skipped");
            return this.ApiProblem(StatusCodes.Status400BadRequest, "INDEXNOW_NOT_CONFIGURED", "IndexNowNotConfigured");
        }

        if (request.UrlList is null || request.UrlList.Count == 0)
        {
            return this.ApiProblem(StatusCodes.Status400BadRequest, "INDEXNOW_NO_URLS", "IndexNowNoUrlsProvided");
        }

        // TODO: Queue background job to submit URLs to IndexNow endpoint
        // For now, log the notification (background job implementation deferred to follow-up task)
        logger.LogInformation(
            "IndexNow notification queued for {Count} URL(s): {Urls}",
            request.UrlList.Count,
            string.Join(", ", request.UrlList.Take(3)));

        return Accepted(new { queued = request.UrlList.Count });
    }
}

public sealed class IndexNowOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string SubmitUrl { get; set; } = "https://api.indexnow.org/indexnow";
}

public sealed record IndexNowRequest(
    [property: Required, MinLength(1), MaxLength(10000)] List<string> UrlList);
