using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Terms of Service, Privacy notice and DPA of the configured versions (LEGAL-TEXTS, decision D9). A document is
/// published only when the text of its configured version exists and every value it needs (the controller's data, the
/// governing court) is configured: otherwise the public page stays "in preparation". That is a configuration to
/// complete, never a startup failure, so the check is <c>degraded</c> and its description (visible to platform admins
/// only) names the Railway variables to set, never their values. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public sealed class LegalDocumentsHealthCheck(ILegalDocumentService legalDocuments) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = GetProblems(legalDocuments);
        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy("Terms, Privacy notice and DPA are published.")
            : HealthCheckResult.Degraded("Legal documents not published: " + string.Join(" | ", problems)));
    }

    /// <summary>One line per document that is not published (or has a text that cannot be); empty when all are.</summary>
    public static IReadOnlyList<string> GetProblems(ILegalDocumentService legalDocuments) =>
        Enum.GetValues<LegalDocumentKind>()
            .Select(legalDocuments.GetPublication)
            .Where(publication => !publication.IsPublished)
            .Select(publication => publication.Describe())
            .ToList();
}
