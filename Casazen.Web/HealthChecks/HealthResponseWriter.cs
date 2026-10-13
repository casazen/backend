using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// JSON body of the health endpoints: overall status, commit of the running build and, per check, name and status.
/// The endpoints are anonymous, so nothing else is written: no description, no exception message, no configuration
/// value. A platform admin (Auth0 role <c>Admin</c>) also gets each check's description, which names the missing
/// variables but never their values; an exception message is replaced by a generic text (details are in the logs).
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var includeDescriptions = context.User.IsInRole("Admin");
        var build = context.RequestServices.GetRequiredService<BuildInfo>();

        var response = new HealthResponse(
            ToText(report.Status),
            build.CommitSha,
            report.Entries
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new HealthCheckResponse(
                    e.Key,
                    ToText(e.Value.Status),
                    includeDescriptions ? GetSafeDescription(e.Value) : null))
                .ToList());

        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(response, JsonOptions, context.RequestAborted);
    }

    public static string ToText(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };

    // A check that throws (or times out) gets the exception message as description: never forward it.
    private static string? GetSafeDescription(HealthReportEntry entry) =>
        entry.Exception is not null && string.Equals(entry.Description, entry.Exception.Message, StringComparison.Ordinal)
            ? "Check failed: see the application logs."
            : entry.Description;

    public sealed record HealthResponse(string Status, string? Commit, IReadOnlyList<HealthCheckResponse> Checks);

    public sealed record HealthCheckResponse(string Name, string Status, string? Description);
}
