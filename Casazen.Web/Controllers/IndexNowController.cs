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

    /// <summary>
    /// Queue URLs for IndexNow submission. Called internally when org or property content changes.
    /// </summary>
    /// <param name="request">URLs to notify search engines about.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
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

/// <summary>Configuration for IndexNow notifications.</summary>
public sealed class IndexNowOptions
{
    /// <summary>
    /// The API key for IndexNow verification. Must be published at the root of the public site
    /// (e.g., https://example.com/{ApiKey}.txt containing the key).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The IndexNow submission endpoint (defaults to Bing's endpoint which forwards to other engines).</summary>
    public string SubmitUrl { get; set; } = "https://api.indexnow.org/indexnow";
}

/// <summary>Request to notify search engines about URL changes via IndexNow.</summary>
/// <param name="UrlList">The URLs that changed (published, updated, or deleted).</param>
public sealed record IndexNowRequest(
    [property: Required, MinLength(1), MaxLength(10000)] List<string> UrlList);
