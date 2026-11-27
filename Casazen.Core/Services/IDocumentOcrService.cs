namespace Casazen.Core.Services;

public interface IDocumentOcrService
{
    Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);

    async Task<DocumentOcrObservation> ObserveAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        new(await ExtractAsync(imageStream, cancellationToken));
}

public sealed record DocumentOcrObservation(DocumentOcrResult? Fields, string? Failure = null);

public class DocumentOcrResult
{
    public string? FullName { get; set; }
    public string? DocumentKind { get; set; }
    public string? BirthPlace { get; set; }
    public string? BirthProvince { get; set; }
    public string? ResidenceStreet { get; set; }
    public string? ResidencePostalCode { get; set; }
    public string? ResidenceCityName { get; set; }
    public string? ResidenceProvince { get; set; }

    public bool HasDocumentEvidence { get; set; }
    public string? GivenNames { get; set; }
    public string? Surname { get; set; }

    public string? DocumentNumber { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public string? Nationality { get; set; }

    public DateTime? ExpirationDate { get; set; }

    public bool? IsExpired { get; set; }

    public DateTime? DateOfIssue { get; set; }

    public string? PlaceOfIssue { get; set; }

    public string? Sex { get; set; }

    public double Confidence { get; set; }

    public IReadOnlyList<string> Quality { get; set; } = [];
}
