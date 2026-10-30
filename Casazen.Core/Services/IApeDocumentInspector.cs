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

    /// <summary>What was read on the certificate, whatever the status: an incomplete one can still give its class.</summary>
    public ApeReading Reading { get; init; } = ApeReading.Empty;
}

/// <summary>
/// The data read on an APE besides the checks: the identification code and the energy class a lease contract states (LT-10),
/// the dates, the EP gl,nren index (kWh/m² year) and whether it is the model of Regione Lombardia (its register, CENED, is
/// public). Each is null when it was not read.
/// </summary>
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
