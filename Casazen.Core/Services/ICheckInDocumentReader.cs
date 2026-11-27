namespace Casazen.Core.Services;

/// <summary>Limits of the photo the guest uploads at check-in.</summary>
public static class CheckInDocumentLimits
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
}

/// <summary>
/// Reads an identity document the guest uploads at the start of check-in, the prefill of <c>read-document</c>: a set of fields the guest still
/// confirms. The picture is read in the process only (the MRZ, then the visual zone), never sent to an AI provider or to any other service, whatever
/// the configuration (E70/F02/US02/T01, NF-197, ADR-013-R21); it is not stored and not logged.
/// </summary>
public interface ICheckInDocumentReader
{
    Task<CheckInDocumentRead> ReadAsync(Stream content, CancellationToken cancellationToken = default);
}

/// <param name="Read">True when at least one field was taken from the document.</param>
/// <param name="Reason">
/// Why nothing was read: <c>unavailable</c> (the local reader could not run), <c>unreadable</c>, <c>not_document</c>,
/// <c>unsupported_file</c>, <c>too_large</c>. Null when <paramref name="Read"/> is true.
/// </param>
public sealed record CheckInDocumentRead(bool Read, string? Reason, CheckInDocumentFields? Fields, string? Side = null)
{
    public static CheckInDocumentRead Failed(string reason) => new(false, reason, null);
}

/// <summary>Fields taken from one document. Anything that does not have a plausible shape is left null.</summary>
public sealed record CheckInDocumentFields(
    string? FirstName,
    string? LastName,
    string? Gender,
    string? DateOfBirth,
    bool? BornInItaly,
    string? BirthPlace,
    string? BirthProvince,
    string? Citizenship,
    string? DocumentKind,
    string? DocumentNumber,
    string? IssuePlace,
    string? DocumentIssuedOn = null,
    string? DocumentExpiresOn = null,
    string? ResidenceStreet = null,
    string? ResidencePostalCode = null,
    string? ResidenceCityName = null,
    string? ResidenceProvince = null,
    string? ResidenceCountryName = null,
    bool? ResidenceInItaly = null);
