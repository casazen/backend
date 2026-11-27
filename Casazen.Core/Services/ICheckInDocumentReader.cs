using Casazen.Core.Identity;

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

    /// <summary>
    /// The prefill of the self-hosted flow (<c>Features:CheckInIdentitySelfHosted</c>, E71/F01/US03/T01, NF-190): the answer of <see cref="ReadAsync"/> and, on top of it, the
    /// fields read by the zones of the card with the side, the zone and the confidence each comes from, and the fields of the check-in the guest has still to type. In the process only.
    /// </summary>
    Task<CheckInZonedRead> ReadZonedAsync(Stream content, CancellationToken cancellationToken = default);
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

/// <summary>
/// The zoned prefill of one photo of an identity document (<see cref="ICheckInDocumentReader.ReadZonedAsync"/>, E71/F01/US03/T01, NF-190): the answer of the local reading as
/// it always was, the values read by the zones of the card, and what the guest has still to type. <b>Personal data</b> that goes to the guest who sent the photo and nowhere else:
/// it is never stored, logged or put in a job (<see cref="ToString"/> leaves the values out).
/// </summary>
/// <param name="Read">The reading of the MRZ and of the visual zone, as <see cref="ICheckInDocumentReader.ReadAsync"/> gives it.</param>
/// <param name="Fields">The values read by zone, each with its side, its zone, its confidence and its source; empty when the card could not be set upright and cut, or nothing was read.</param>
/// <param name="Missing">The fields of <see cref="CheckInPrefillFields.Asked"/> that neither the reading nor the zones gave, in that order: what the guest must still type.</param>
public sealed record CheckInZonedRead(CheckInDocumentRead Read, IReadOnlyList<ZonedField> Fields, IReadOnlyList<string> Missing)
{
    /// <summary>What was asked and what is still missing, never a value.</summary>
    public override string ToString() => $"CheckInZonedRead {{ Read = {Read.Read}, Fields = {Fields.Count}, Missing = {Missing.Count} }}";

    /// <summary>The answer when nothing could be read by zone: no field, and everything asked is missing.</summary>
    public static CheckInZonedRead Of(CheckInDocumentRead read) => new(read, [], CheckInPrefillFields.MissingFrom(read.Fields, []));
}

/// <summary>
/// The fields of the check-in a document can fill, by the name of the member of <see cref="CheckInDocumentFields"/> in camel case (the names of <c>missing</c> in the answer of the
/// self-hosted <c>read-document</c>, E71/F01/US03/T01). <see cref="Asked"/> are the ones of the Alloggiati record every guest gives: what the page asks for when the photo gave none.
/// </summary>
public static class CheckInPrefillFields
{
    public const string LastName = "lastName";
    public const string FirstName = "firstName";
    public const string Gender = "gender";
    public const string DateOfBirth = "dateOfBirth";
    public const string BirthPlace = "birthPlace";
    public const string Citizenship = "citizenship";
    public const string DocumentKind = "documentKind";
    public const string DocumentNumber = "documentNumber";
    public const string IssuePlace = "issuePlace";

    /// <summary>Every field the document can fill and the check-in asks for, in this order.</summary>
    public static IReadOnlyList<string> Asked { get; } =
        [LastName, FirstName, Gender, DateOfBirth, BirthPlace, Citizenship, DocumentKind, DocumentNumber, IssuePlace];

    /// <summary>
    /// The fields of <see cref="Asked"/> that <paramref name="read"/> (the reading of today) and <paramref name="zoned"/> (the zones) both leave empty: a field is filled when the
    /// reading holds a value for it or a zone of the card gave the field it comes from (<see cref="ZonedFieldNames"/>). The kind of document and the place of issue have no zone: the
    /// reading alone fills them.
    /// </summary>
    public static IReadOnlyList<string> MissingFrom(CheckInDocumentFields? read, IReadOnlyList<ZonedField> zoned)
    {
        ArgumentNullException.ThrowIfNull(zoned);

        return [.. Asked.Where(name => !IsFilled(name, read, zoned))];
    }

    private static bool IsFilled(string name, CheckInDocumentFields? read, IReadOnlyList<ZonedField> zoned)
    {
        var fromRead = name switch
        {
            LastName => read?.LastName,
            FirstName => read?.FirstName,
            Gender => read?.Gender,
            DateOfBirth => read?.DateOfBirth,
            BirthPlace => read?.BirthPlace,
            Citizenship => read?.Citizenship,
            DocumentKind => read?.DocumentKind,
            DocumentNumber => read?.DocumentNumber,
            IssuePlace => read?.IssuePlace,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(fromRead))
            return true;

        var zone = name switch
        {
            LastName => ZonedFieldNames.Surname,
            FirstName => ZonedFieldNames.GivenNames,
            Gender => ZonedFieldNames.Sex,
            DateOfBirth => ZonedFieldNames.BirthDate,
            BirthPlace => ZonedFieldNames.BirthPlace,
            Citizenship => ZonedFieldNames.Nationality,
            DocumentNumber => ZonedFieldNames.DocumentNumber,
            _ => null,
        };
        return zone is not null && zoned.Any(field => string.Equals(field.Name, zone, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(field.Value));
    }
}
