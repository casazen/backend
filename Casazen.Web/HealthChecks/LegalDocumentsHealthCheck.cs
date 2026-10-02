using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Legal documents and subprocessor list (PL-14, D14, DEPLOY-CFG). The texts of Terms of Service, Privacy notice and DPA
/// are provided by the product owner and never written by code; until they are, the public pages say "in preparazione"
/// and the onboarding asks hosts to accept a version nobody can read. Nothing here stops the API (it works without the
/// texts), so every gap is <c>degraded</c>: no text and no external copy for the configured version, no date in force,
/// subprocessors with details still "in definizione" or without a date in force. The description names the
/// configuration keys and the version, never a value. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public sealed class LegalDocumentsHealthCheck(ILegalDocumentService legalDocuments) : IHealthCheck
{
    private static readonly LegalDocumentKind[] Kinds = [LegalDocumentKind.Tos, LegalDocumentKind.Privacy, LegalDocumentKind.Dpa];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        foreach (var kind in Kinds)
        {
            var meta = legalDocuments.Get(kind);
            if (meta.DocumentUrl is null && legalDocuments.GetText(kind, LegalDocumentService.DefaultLanguage) is null)
            {
                problems.Add(
                    $"{kind} {meta.Version}: no text (LegalDocuments/{kind.ToString().ToLowerInvariant()}/{meta.Version}.it.html) " +
                    $"and no Legal__Documents__{kind}__DocumentUrl.");
            }

            if (meta.EffectiveAt is null)
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
            : HealthCheckResult.Degraded("Legal documents incomplete: " + string.Join(" ", problems)));
    }
}
