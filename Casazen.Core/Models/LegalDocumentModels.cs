namespace Casazen.Core.Models;

public record LegalDocumentMeta(string Version, DateTime EffectiveAt, string Title, string Summary, string? DocumentUrl);
/// <param name="TransferMechanism">Legal basis of a transfer outside the EEA, when there is one (GDPR chapter V).</param>
/// <param name="DetailsPending">Location or transfer mechanism still to be completed (never invented by code).</param>
public record SubprocessorItem(
    string Name,
    string Purpose,
    string Region,
    string? Website,
    string? TransferMechanism = null,
    bool DetailsPending = false);
public record SubprocessorsDocument(string Version, DateTime EffectiveAt, IReadOnlyList<SubprocessorItem> Items);
