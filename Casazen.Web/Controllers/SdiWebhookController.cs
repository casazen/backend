using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.External.Sdi;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The webhook of the SDI channel (E17/F01/US02/T02, NF-116, ADR-022): the provider calls it when the SDI answered a submitted invoice. The provider's call carries no CasaZen credential, so it is
/// authenticated by the secret in its address (<see cref="SdiWebhookVerifier"/>, compared in constant time) and its body is a notification only: the adapter reads what it names again from the provider's API,
/// and every notification goes to the one entry of the outcomes, <see cref="IFiscalDocumentSdiService.RecordOutcomeAsync"/>, under the tenant of the org of the document.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The secret and the body are never logged.</b> A wrong secret is 401 and a security event in the log (never the secret, never the body). A missing or short <c>Sdi:Provider:WebhookSecret</c>, or
/// no provider configured, is 500: never an accepted call. The secret is part of the path: the request logs must not print the path of <c>/api/webhooks/sdi/*</c> (the error middleware logs the route
/// template only; <c>docs/runbooks/fiscal-documents.md</c>).</item>
/// <item><b>Retried by the provider.</b> A notification about a submission CasaZen does not know yet (the call may overtake the save of the submission) is 404 <c>sdi_transmission_unknown</c>, and a failure of
/// the provider's API or of the database is 500: the provider delivers the call again, and the job <c>fiscal-sdi-status</c> reads the state anyway. What was recorded stays recorded (the outcomes are
/// idempotent). An event that is not about a submission is acknowledged with 200 and recorded nowhere.</item>
/// <item>Behind <c>Features:SdiSubmission</c> and <c>Features:FiscalDocumentIssuance</c>: 404 while either is off, before anything is read.</item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/webhooks/sdi/{secret}")]
[AllowAnonymous]
[FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
[FeatureGate(FeatureFlags.SdiSubmission)]
public sealed class SdiWebhookController(
    SdiWebhookVerifier verifier,
    ISdiEInvoiceProvider provider,
    IServiceScopeFactory scopeFactory,
    ILogger<SdiWebhookController> logger) : ControllerBase
{
    /// <summary>The largest body read: a notification names an event, not an invoice.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    private static readonly EventId SecretRefusedEvent = new(52_201, "SdiWebhookSecretRefused");

    /// <summary>Receives one notification of the provider.</summary>
    /// <response code="200">Recorded for the documents it is about, or acknowledged with nothing to record.</response>
    /// <response code="401"><c>invalid_webhook_secret</c>: the secret of the address is not the configured one.</response>
    /// <response code="404"><c>sdi_transmission_unknown</c>: no document of CasaZen has the submission it names (yet).</response>
    /// <response code="500"><c>sdi_webhook_not_configured</c>: no usable secret or no provider configured.</response>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Receive(string secret, CancellationToken cancellationToken)
    {
        if (!verifier.HasUsableSecret || !provider.IsConfigured)
        {
            logger.LogError("SDI webhook not configured (Sdi__Provider__WebhookSecret, 32+ characters, and the provider's key): the call is refused");
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "sdi_webhook_not_configured", "SdiWebhookNotConfigured");
        }

        if (!verifier.IsValid(secret))
        {
            logger.LogWarning(
                SecretRefusedEvent,
                "Security: SDI webhook refused, the secret of the address does not match (remote {RemoteIp})",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return this.ApiProblem(StatusCodes.Status401Unauthorized, "invalid_webhook_secret", "SdiWebhookSecretInvalid");
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync(cancellationToken);

        // Only the adapter knows the provider's wire format; a failure to read what the call names is thrown (500), so the provider calls again.
        var notifications = await provider.ReadNotificationAsync(body, cancellationToken);
        if (notifications.Count == 0)
        {
            logger.LogInformation("SDI webhook acknowledged without effect: the call is not about a submission");
            return Ok();
        }

        var unknown = 0;
        var recorded = 0;
        foreach (var notification in notifications)
        {
            // The org of the document is found across the orgs (a system read), then the outcome is recorded in a scope of its own, under that org's tenant.
            SdiSubmittedDocument? document;
            await using (var finding = scopeFactory.CreateAsyncScope())
                document = await finding.ServiceProvider.GetRequiredService<IFiscalDocumentSdiService>().FindByTransmissionAsync(notification.TransmissionId, cancellationToken);

            if (document is null)
            {
                unknown++;
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(document.OrgId);
            var result = await scope.ServiceProvider.GetRequiredService<IFiscalDocumentSdiService>()
                .RecordOutcomeAsync(document.DocumentId, SdiOutcome.Of(notification.Outcome), cancellationToken);
            if (result == SdiRecordResult.Recorded)
                recorded++;
        }

        logger.LogInformation("SDI webhook: {Notifications} notifications, {Recorded} outcomes recorded, {Unknown} about a submission not known (yet)", notifications.Count, recorded, unknown);
        return unknown > 0
            ? this.ApiProblem(StatusCodes.Status404NotFound, "sdi_transmission_unknown", "SdiTransmissionUnknown")
            : Ok();
    }
}
