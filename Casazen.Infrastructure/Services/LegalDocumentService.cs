using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public class LegalDocumentService(IConfiguration configuration) : ILegalDocumentService
{
    private string GetVersion(string key) =>
        configuration[$"Legal:Documents:{key}:Version"] ?? "1.0";

    public LegalDocumentMeta GetTos() => new(
        Version: GetVersion("Tos"),
        EffectiveAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Title: "Termini di Servizio",
        Summary: "Condizioni generali di utilizzo della piattaforma CasaZen.",
        DocumentUrl: null);

    public LegalDocumentMeta GetPrivacy() => new(
        Version: GetVersion("Privacy"),
        EffectiveAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Title: "Informativa Privacy",
        Summary: "Informativa sul trattamento dei dati personali ai sensi del GDPR.",
        DocumentUrl: null);

    public LegalDocumentMeta GetDpa() => new(
        Version: GetVersion("Dpa"),
        EffectiveAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Title: "Data Processing Agreement",
        Summary: "Accordo sul trattamento dei dati (Art. 28 GDPR).",
        DocumentUrl: null);

    public SubprocessorsDocument GetSubprocessors()
    {
        var version = GetVersion("Subprocessors");
        var items = ReadSubprocessorItems();
        return new SubprocessorsDocument(version, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), items);
    }

    private IReadOnlyList<SubprocessorItem> ReadSubprocessorItems()
    {
        var section = configuration.GetSection("Legal:Documents:Subprocessors:Items");
        var children = section.GetChildren().ToList();

        if (children.Count == 0)
        {
            return
            [
                new SubprocessorItem("Supabase", "Database", "EU", null),
                new SubprocessorItem("Auth0", "Authentication", "EU", null),
                new SubprocessorItem("Stripe", "Payments", "EU", null),
                new SubprocessorItem("SendGrid", "Email", "EU", null),
            ];
        }

        return children.Select(c => new SubprocessorItem(
            Name: c["Name"] ?? string.Empty,
            Purpose: c["Purpose"] ?? string.Empty,
            Region: c["Region"] ?? string.Empty,
            Website: c["Website"])).ToList();
    }
}
