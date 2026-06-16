namespace Casazen.Core.Models;

public record LegalDocumentMeta(string Version, DateTime EffectiveAt, string Title, string Summary, string? DocumentUrl);
public record SubprocessorItem(string Name, string Purpose, string Region, string? Website);
public record SubprocessorsDocument(string Version, DateTime EffectiveAt, IReadOnlyList<SubprocessorItem> Items);
