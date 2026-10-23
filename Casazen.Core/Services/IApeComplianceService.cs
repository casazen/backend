using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IApeComplianceService
{
    /// <summary>
    /// Throws unless the property has an APE a contract can rely on: one whose code and energy class the landlord typed (LT-10,
    /// what the contract prints), or one whose extracted text passes the APE checks. With neither, an expired certificate is
    /// refused as such and any other one asks for its code and energy class (<see cref="ApeComplianceException.IdentificationRequiredCode"/>).
    /// </summary>
    Task EnsurePropertyHasValidApeAsync(Guid propertyId);

    /// <summary>
    /// Reads an APE being uploaded (OCR for images and scanned PDFs). Throws only for a file that cannot be a certificate still in
    /// force: not a PDF/JPEG/PNG, unreadable, or expired by the date read on it. A certificate whose data could not all be read (a
    /// photo, a photocopy, a regional model the reader does not know, the reader not available) is accepted: the answer says what
    /// was not read, and the landlord confirms the certificate by typing its code and energy class.
    /// </summary>
    Task<ApeUploadCheck> CheckUploadedApeAsync(IFormFile file);
}

/// <summary>What the automatic reading of an uploaded APE found.</summary>
/// <param name="Outcome">
/// <c>Verified</c> (every field read), <c>Incomplete</c> (recognised, some fields not read: <paramref name="UnreadFields"/>),
/// <c>NotRecognized</c> (the title of an APE was not found) or <c>NotChecked</c> (the reader is not available).
/// </param>
/// <param name="UnreadFields">The fields not read, as the inspector names them (<c>CertificateCode</c>, <c>ExpiryDate</c>, ...).</param>
public sealed record ApeUploadCheck(string Outcome, IReadOnlyList<string> UnreadFields)
{
    public const string Verified = "Verified";
    public const string Incomplete = "Incomplete";
    public const string NotRecognized = "NotRecognized";
    public const string NotChecked = "NotChecked";

    public static ApeUploadCheck Passed { get; } = new(Verified, []);
}

public sealed class ApeComplianceException : InvalidOperationException
{
    public const string RequiredCode = "APE_REQUIRED";
    public const string InvalidContentCode = "APE_INVALID_CONTENT";
    public const string IdentificationRequiredCode = "APE_IDENTIFICATION_REQUIRED";

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
        MessageKey = code switch
        {
            RequiredCode => "ApeRequired",
            IdentificationRequiredCode => "ApeIdentificationRequired",
            _ => "ApeInvalidContent"
        };
    }

    public static ApeComplianceException Required() =>
        new(RequiredCode, "APE document is required before creating a lease contract.");

    public static ApeComplianceException InvalidContent() =>
        new(InvalidContentCode,
            "The uploaded file is not a valid APE (energy performance certificate). The PDF content does not match an official attestato di prestazione energetica.");

    public static ApeComplianceException IdentificationRequired() =>
        new(IdentificationRequiredCode,
            "The APE of the property could not be read automatically: its code and energy class must be entered.");
}
