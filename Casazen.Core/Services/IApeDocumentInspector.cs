namespace Casazen.Core.Services;

public enum ApeInspectionStatus
{
    Valid,
    NotAPdf,
    Unreadable,
    ContentMismatch,
    Incomplete,
    Expired,
    OcrUnavailable
}

public sealed record ApeInspectionResult(ApeInspectionStatus Status, IReadOnlyList<string>? MissingFields = null)
{
    public bool IsValid => Status == ApeInspectionStatus.Valid;
}

public interface IApeDocumentInspector
{
    ApeInspectionResult Inspect(Stream content);
    Task<ApeInspectionResult> InspectAsync(Stream content, CancellationToken ct = default) => Task.FromResult(Inspect(content));
    ApeInspectionResult InspectExtractedText(string text);
}
