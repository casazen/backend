using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IApeComplianceService
{
    /// <summary>Throws if the property has no official APE whose PDF content can be verified.</summary>
    Task EnsurePropertyHasValidApeAsync(Guid propertyId);

    /// <summary>Throws if the uploaded file is not an official APE PDF.</summary>
    Task EnsureUploadedFileIsOfficialApeAsync(IFormFile file);
}

public sealed class ApeComplianceException : InvalidOperationException
{
    public const string RequiredCode = "APE_REQUIRED";
    public const string InvalidContentCode = "APE_INVALID_CONTENT";

    public string Code { get; }

    public ApeComplianceException(string code, string message) : base(message)
    {
        Code = code;
    }

    public static ApeComplianceException Required() =>
        new(RequiredCode, "APE document is required before creating a lease contract.");

    public static ApeComplianceException InvalidContent() =>
        new(InvalidContentCode,
            "The uploaded file is not a valid APE (energy performance certificate). The PDF content does not match an official attestato di prestazione energetica.");
}
