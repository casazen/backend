namespace Casazen.Core.Services;

public interface IMrzExtractionService
{
    Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);

    Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default);

    // The picture lies upright (the geometry of the document turned it): it is read as it lies, and turned the other ways only when nothing that looks like an MRZ is found in it.
    // A block that is found and does not pass its check digits is the answer, not a reason to look for it again in three other turns.
    Task<MrzReading> ReadUprightAsync(Stream imageStream, CancellationToken cancellationToken = default) => ReadAsync(imageStream, cancellationToken);
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
