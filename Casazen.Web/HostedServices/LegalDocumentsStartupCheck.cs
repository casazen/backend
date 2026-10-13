using Casazen.Core.Models;
using Casazen.Core.Services;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Says at startup which legal documents are not published and what to configure (LEGAL-TEXTS, decision D9): the same
/// facts as the <c>legal</c> health check, in the deploy log. A missing text or value never stops the application: the
/// public pages stay "in preparation" (fail-closed). Names variables, never values.
/// </summary>
public sealed class LegalDocumentsStartupCheck(
    ILegalDocumentService legalDocuments,
    ILogger<LegalDocumentsStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in Enum.GetValues<LegalDocumentKind>())
        {
            var publication = legalDocuments.GetPublication(kind);
            if (publication.IsPublished)
            {
                logger.LogInformation("Legal document {Kind} version {Version} is published", kind, publication.Version);
                continue;
            }

            logger.LogWarning(
                "Legal document not published, the public page stays 'in preparation': {Description}. " +
                "See docs/runbooks/legal-documents.md",
                publication.Describe());
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
