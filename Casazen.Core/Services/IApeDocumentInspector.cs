namespace Casazen.Core.Services;

public enum ApeInspectionStatus
{
    Valid,
    NotAPdf,
    Unreadable,
    ContentMismatch
}

public sealed record ApeInspectionResult(ApeInspectionStatus Status)
{
    public bool IsValid => Status == ApeInspectionStatus.Valid;
}

public interface IApeDocumentInspector
{
    ApeInspectionResult Inspect(Stream content);
    ApeInspectionResult InspectExtractedText(string text);
}
