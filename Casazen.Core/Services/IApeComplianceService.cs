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
    /// was not read. It also gives the code and the energy class read on the file; a code that cannot be read is asked to the public
    /// register of the region (<see cref="IApeRegistryLookup"/>, Lombardy) with the comune of <paramref name="context"/> and the
    /// index read on the file.
    /// </summary>
    Task<ApeUploadCheck> CheckUploadedApeAsync(IFormFile file, ApeRegistryContext? context = null);

    /// <summary>
    /// Reads again an APE already stored (one uploaded before its code and class were read, or confirmed by hand): the same reading
    /// and register as <see cref="CheckUploadedApeAsync"/>, without refusing anything (<c>Expired</c> is an outcome here).
    /// </summary>
    Task<ApeUploadCheck> ReadStoredApeAsync(Stream content, ApeRegistryContext? context = null, CancellationToken cancellationToken = default);
}

/// <summary>Where the property of an APE is: the comune (as the register writes it) and CasaZen's code of its region (<c>LOM</c>).</summary>
public sealed record ApeRegistryContext(string? Comune, string? RegionCode);

/// <summary>What the automatic reading of an uploaded APE found.</summary>
/// <param name="Outcome">
/// <c>Verified</c> (every field read), <c>Incomplete</c> (recognised, some fields not read: <paramref name="UnreadFields"/>),
/// <c>NotRecognized</c> (the title of an APE was not found), <c>NotChecked</c> (the reader is not available) or, only when a
/// stored APE is read again, <c>Expired</c>.
/// </param>
/// <param name="UnreadFields">The fields not read, as the inspector names them (<c>CertificateCode</c>, <c>ExpiryDate</c>, ...).</param>
public sealed record ApeUploadCheck(string Outcome, IReadOnlyList<string> UnreadFields)
{
    public const string Verified = "Verified";
    public const string Incomplete = "Incomplete";
    public const string NotRecognized = "NotRecognized";
    public const string NotChecked = "NotChecked";
    public const string Expired = "Expired";

    /// <summary>The code came from the file itself.</summary>
    public const string FromDocument = "Document";

    /// <summary>The code came from the public register (the file did not show it legibly).</summary>
    public const string FromRegistry = "Registry";

    public static ApeUploadCheck Passed { get; } = new(Verified, []);

    /// <summary>The identification code found (on the file or in the register); null when none was.</summary>
    public string? CertificateCode { get; init; }

    /// <summary>The energy class found (A4...G); null when none was.</summary>
    public string? EnergyClass { get; init; }

    /// <summary>Where <see cref="CertificateCode"/> came from: <see cref="FromDocument"/> or <see cref="FromRegistry"/>.</summary>
    public string? CodeSource { get; init; }
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
