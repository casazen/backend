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

    public ApeReading Reading { get; init; } = ApeReading.Empty;
}

public sealed record ApeReading(
    string? CertificateCode,
    string? EnergyClass,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    decimal? GlobalNonRenewableIndex,
    bool LombardyModel)
{
    public static ApeReading Empty { get; } = new(null, null, null, null, null, false);
}

public interface IApeDocumentInspector
{
    ApeInspectionResult Inspect(Stream content);
    Task<ApeInspectionResult> InspectAsync(Stream content, CancellationToken ct = default) => Task.FromResult(Inspect(content));
    ApeInspectionResult InspectExtractedText(string text);
}
