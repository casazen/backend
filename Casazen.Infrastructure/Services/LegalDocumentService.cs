using Casazen.Core.Models;
using Casazen.Core.Options;
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

    /// <summary>
    /// The configured subprocessors plus, when an external AI provider is active (<see cref="AiOptions"/>), that
    /// provider (A8-15): prompts reach it, so it must be declared. Adding it changes the version (<c>+ai-…</c>), so
    /// every host acknowledges the new list again during onboarding.
    /// </summary>
    public SubprocessorsDocument GetSubprocessors()
    {
        var version = GetVersion("Subprocessors");
        var items = ReadSubprocessorItems().ToList();

        var aiProvider = BuildActiveAiProviderItem();
        if (aiProvider is not null
            && !items.Any(item => string.Equals(item.Name, aiProvider.Name, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(aiProvider);
            version = $"{version}+ai-{aiProvider.Name.ToLowerInvariant().Replace(' ', '-')}";
        }

        return new SubprocessorsDocument(version, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), items);
    }

    /// <summary>
    /// Entry of the active external AI provider from <c>Ai:Subprocessor</c>. Location and transfer mechanism are legal
    /// facts about the provider: never filled in here; while missing the entry is marked <c>DetailsPending</c>.
    /// </summary>
    private SubprocessorItem? BuildActiveAiProviderItem()
    {
        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        if (!ai.IsExternalProviderActive())
            return null;

        var details = ai.Subprocessor;
        var region = details.Region?.Trim() ?? string.Empty;
        var transferMechanism = string.IsNullOrWhiteSpace(details.TransferMechanism) ? null : details.TransferMechanism.Trim();
        return new SubprocessorItem(
            Name: string.IsNullOrWhiteSpace(details.Name) ? ai.Provider.Trim() : details.Name.Trim(),
            Purpose: string.IsNullOrWhiteSpace(details.Purpose) ? "AI text generation" : details.Purpose.Trim(),
            Region: region,
            Website: string.IsNullOrWhiteSpace(details.Website) ? null : details.Website.Trim(),
            TransferMechanism: transferMechanism,
            DetailsPending: region.Length == 0 || transferMechanism is null);
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
            Website: c["Website"],
            TransferMechanism: string.IsNullOrWhiteSpace(c["TransferMechanism"]) ? null : c["TransferMechanism"],
            DetailsPending: string.IsNullOrWhiteSpace(c["Region"]))).ToList();
    }
}
