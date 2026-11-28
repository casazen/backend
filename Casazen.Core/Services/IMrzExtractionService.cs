namespace Casazen.Core.Services;

public interface IMrzExtractionService
{
    Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);

    Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

public sealed record MrzReading(MrzExtractionResult? Result, IReadOnlyList<string> Quality);

public static class MrzQualityCodes
{
    public const string MrzNotFound = "mrz_not_found";

    public const string Glare = "glare";

    public const string Blur = "blur";
}

public class MrzExtractionResult
{
    public string DocumentType { get; set; } = string.Empty;

    public string? Surname { get; set; }

    public string? GivenNames { get; set; }

    public string? Nationality { get; set; }

    public string? DocumentNumber { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public DateTime? ExpirationDate { get; set; }

    public bool? IsExpired { get; set; }

    public string? Sex { get; set; }

    public string? IssuingState { get; set; }

    public string? DocumentCode { get; set; }

    public string? MrzType { get; set; }

    public bool CheckDigitsValid { get; set; }

    public double Confidence { get; set; }

    public IReadOnlyList<string> Quality { get; set; } = [];
}
