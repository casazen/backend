using System.Text;
using Casazen.Core.Features;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Identity;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Casazen.Web.Controllers;

/// <summary>
/// The export of the pilot of the self-hosted identity verification (E73/F01/US03/T02, NF-196, decision D10): the CSV that sets the shadow decision of the policy next to the
/// decision of a person for each identity step opened in the period, for the pilot of 500 consecutive check-ins with a parallel human verification. It sits under the same policy as
/// the rest of the operator queue (<c>verification.decision</c>: the owner, the administrators and the property managers, for the properties they reach), takes the same period and
/// the same scope as <see cref="GetMetrics"/>, and is behind <see cref="FeatureFlags.CheckInIdentityVerification"/> and <see cref="FeatureFlags.CheckInIdentitySelfHosted"/>, both off
/// by default. Its services come as <c>[FromServices]</c> parameters, so the constructor of the class does not change; they are registered in
/// <c>IdentityVerificationServiceCollectionExtensions.Metrics.cs</c>.
/// </summary>
public partial class VerificationController
{
    /// <summary>
    /// The CSV of the pilot, <c>text/csv; charset=utf-8</c> with a byte order mark, RFC 4180, file name <c>identity-verifications-&lt;from&gt;-&lt;to&gt;.csv</c> (the first and the last day
    /// of the period): the header <c>attempt_ref,property_id,opened_at,closed_at,format_id,channel,outcome_code,decision_mode,shadow_outcome,reason_codes,guided_retries,human_outcome,
    /// human_decided_at,review_seconds,duration_ms,policy_version,policy_hash_prefix</c> and one row for each identity step opened in the period by the bookings the caller reaches. No
    /// image, name, e-mail, document number or score of a person is in it (<c>attempt_ref</c> is a pseudonym of the session); it is streamed, never cached, adds no entry to the activity
    /// log (like the other exports) and its content is never logged. The period is <paramref name="from"/> to <paramref name="to"/>, days in Europe/Rome, both included, by default the
    /// last 30 days; a period that ends before it starts or is longer than 366 days is 400 <c>verification_metrics_period_invalid</c>, as for the metrics. 404 while
    /// <see cref="FeatureFlags.CheckInIdentityVerification"/> or <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> is off, before anything is read.
    /// </summary>
    [HttpGet("metrics/export.csv")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportPilot(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromServices] IFeatureFlags featureFlags,
        [FromServices] IdentityPilotExport pilotExport,
        CancellationToken cancellationToken)
    {
        if (!SelfHostedIdentityOn(featureFlags))
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        if (!TryResolvePeriod(from, to, out var first, out var last))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "verification_metrics_period_invalid", "VerificationMetricsPeriodInvalid");

        var file = await pilotExport.OpenAsync(scope, RomeCalendar.StartOfDayUtc(first), RomeCalendar.StartOfDayUtc(last.AddDays(1)), cancellationToken);

        logger.LogInformation("User {UserId} exported the CSV of the identity pilot, {Rows} rows, {From} to {To}", userId, file.RowCount, first, last);
        return new PilotCsvResult(file, IdentityPilotExport.FileName(first, last));
    }

    private sealed class PilotCsvResult(IdentityPilotFile file, string fileName) : IActionResult
    {
        private static readonly byte[] Utf8Bom = Encoding.UTF8.GetPreamble();

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = IdentityPilotExport.ContentType;
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();

            var cancellationToken = context.HttpContext.RequestAborted;
            await response.Body.WriteAsync(Utf8Bom, cancellationToken);
            await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            foreach (var line in file.Lines())
                await writer.WriteAsync(line.AsMemory(), cancellationToken);

            await writer.FlushAsync(cancellationToken);
        }
    }
}
