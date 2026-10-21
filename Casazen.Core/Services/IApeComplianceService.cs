using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IApeComplianceService
{
    /// <summary>Throws if the property has no official APE whose extracted text passes the APE checks.</summary>
    Task EnsurePropertyHasValidApeAsync(Guid propertyId);

    /// <summary>Validates APE text and fundamental fields, using OCR for images and scanned PDFs.</summary>
    Task EnsureUploadedFileIsOfficialApeAsync(IFormFile file);
}

public sealed class ApeComplianceException : InvalidOperationException
{
    public const string RequiredCode = "APE_REQUIRED";
    public const string InvalidContentCode = "APE_INVALID_CONTENT";

    public string Code { get; }
    public IReadOnlyList<string> MissingFields { get; } = [];
    public string MessageKey { get; } = "ApeInvalidContent";
    public bool IsUnavailable { get; }

    public ApeComplianceException(ApeInspectionResult result) : base("APE verification failed")
    {
        IsUnavailable = result.Status == ApeInspectionStatus.OcrUnavailable;
        Code = IsUnavailable ? "APE_OCR_UNAVAILABLE" : InvalidContentCode;
        MissingFields = result.MissingFields ?? [];
        MessageKey = result.Status switch
        {
            ApeInspectionStatus.Incomplete => "ApeIncomplete",
            ApeInspectionStatus.Expired => "ApeExpired",
            ApeInspectionStatus.OcrUnavailable => "ApeOcrUnavailable",
            _ => "ApeInvalidContent"
        };
    }


    public ApeComplianceException(string code, string message) : base(message)
    {
        Code = code;
        MessageKey = code == RequiredCode ? "ApeRequired" : "ApeInvalidContent";
    }

    public static ApeComplianceException Required() =>
        new(RequiredCode, "APE document is required before creating a lease contract.");

    public static ApeComplianceException InvalidContent() =>
        new(InvalidContentCode,
            "The uploaded file is not a valid APE (energy performance certificate). The PDF content does not match an official attestato di prestazione energetica.");
}
