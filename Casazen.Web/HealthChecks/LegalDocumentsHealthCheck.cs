using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Legal documents and subprocessor list (PL-14, LEGAL-TEXTS, DEPLOY-CFG, decision D9). A document is published only
/// when the text of its configured version exists (or an external copy is linked) and every value it needs (the
/// controller's data, the governing court) is configured: otherwise the public page stays "in preparation". Nothing
/// here stops the API, so every gap is <c>degraded</c>: a document not published, no date in force, subprocessors with
/// details still "in definizione" or without a date in force. The description (visible to platform admins only) names
/// the configuration keys and the version, never a value. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public sealed class LegalDocumentsHealthCheck(ILegalDocumentService legalDocuments) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = GetProblems(legalDocuments).ToList();

        foreach (var kind in Enum.GetValues<LegalDocumentKind>())
        {
            if (legalDocuments.Get(kind).EffectiveAt is null)
                problems.Add($"Legal__Documents__{kind}__EffectiveAt is not set.");
        }

        var subprocessors = legalDocuments.GetSubprocessors();
        if (subprocessors.EffectiveAt is null)
            problems.Add("Legal__Documents__Subprocessors__EffectiveAt is not set.");

        var pending = subprocessors.Items.Where(item => item.DetailsPending).Select(item => item.Name).ToList();
        if (pending.Count > 0)
        {
            problems.Add(
                $"Subprocessor details still pending for {string.Join(", ", pending)} " +
                "(Legal__Documents__Subprocessors__Providers__<Provider>__Entity/Region/TransferMechanism).");
        }

        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy("Legal documents and subprocessor list published.")
            : HealthCheckResult.Degraded("Legal documents incomplete: " + string.Join(" | ", problems)));
    }

    /// <summary>One line per document that is not published (or has a text that cannot be); empty when all are.</summary>
    public static IReadOnlyList<string> GetProblems(ILegalDocumentService legalDocuments) =>
        Enum.GetValues<LegalDocumentKind>()
            .Select(legalDocuments.GetPublication)
            .Where(publication => !publication.IsPublished)
            .Select(publication => publication.Describe())
            .ToList();
}
