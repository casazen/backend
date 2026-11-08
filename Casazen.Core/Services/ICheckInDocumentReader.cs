namespace Casazen.Core.Services;

/// <summary>Limits of the photo the guest uploads at check-in.</summary>
public static class CheckInDocumentLimits
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
}

/// <summary>
/// Reads an identity document the guest uploads at the start of check-in. The picture is not stored. When an external
/// AI provider is configured, the picture is sent to it and the answer is a set of fields the guest still confirms.
/// </summary>
public interface ICheckInDocumentReader
{
    Task<CheckInDocumentRead> ReadAsync(Stream content, CancellationToken cancellationToken = default);
}

/// <param name="Read">True when at least one field was taken from the document.</param>
/// <param name="Reason">
/// Why nothing was read: <c>unavailable</c> (no AI provider), <c>unreadable</c>, <c>unsupported_file</c>,
/// <c>too_large</c>, <c>budget</c>. Null when <paramref name="Read"/> is true.
/// </param>
public sealed record CheckInDocumentRead(bool Read, string? Reason, CheckInDocumentFields? Fields)
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
    string? IssuePlace);
