using Casazen.Core.Services;

namespace Casazen.Core.Identity;

/// <summary>
/// One value read on one side of an identity document, with the place it was read from (NF-190: «associa ogni valore alla zona letta»). The value is what the
/// reader of the zone found next to its own label, or what the MRZ says; it is <b>personal data</b>: <see cref="ToString"/> leaves it out, so a record that is
/// logged by mistake does not leak it. Formats of <see cref="Value"/>: names and places as printed in upper case letters; dates as <c>yyyy-MM-dd</c> (invariant);
/// <c>sex</c> as <c>M</c>, <c>F</c> or <c>X</c>; <c>nationality</c> as the three letters of the country; <c>documentNumber</c> as letters and digits; <c>fiscalCode</c>
/// as the 16 characters of a valid code of a person.
/// </summary>
/// <param name="Name">What the value is, a code of <see cref="ZonedFieldNames"/>.</param>
/// <param name="Value">The value, in the form above. Never empty.</param>
/// <param name="Side">The side of the document the value was read on.</param>
/// <param name="ZoneId">The zone it was read from: the name of the field zone of the format (<c>surname</c>, <c>birthDate</c>, ...), or <see cref="ZonedFieldZones.Mrz"/>.</param>
/// <param name="Confidence">
/// How far the reading is trusted, 0 to 1: the confidence the MRZ reading has today (<c>0.95</c>, <c>0.90</c> when the OCR corrected a digit, <c>0.40</c> when a check
/// digit fails), and the local confidence of the visual zone (<c>0.55</c>: the guest always confirms what the visual zone gives).
/// </param>
/// <param name="Source">Where the value comes from, a code of <see cref="ZonedFieldSources"/>.</param>
public sealed record ZonedField(string Name, string Value, DocumentSide Side, string ZoneId, double Confidence, string Source)
{
    /// <summary>The name, the side, the zone and the source: never the value (it is personal data).</summary>
    public override string ToString() => $"ZonedField {{ Name = {Name}, Side = {Side}, ZoneId = {ZoneId}, Source = {Source} }}";
}

public static class ZonedFieldNames
{
    public const string Surname = "surname";
    public const string GivenNames = "givenNames";
    public const string BirthDate = "birthDate";
    public const string BirthPlace = "birthPlace";
    public const string Sex = "sex";
    public const string Nationality = "nationality";
    public const string DocumentNumber = "documentNumber";
    public const string IssueDate = "issueDate";
    public const string ExpiryDate = "expiryDate";
    public const string FiscalCode = "fiscalCode";

    public static IReadOnlyList<string> All { get; } =
        [Surname, GivenNames, BirthDate, BirthPlace, Sex, Nationality, DocumentNumber, IssueDate, ExpiryDate, FiscalCode];
}

public static class ZonedFieldSources
{
    public const string Mrz = "mrz";

    public const string Viz = "viz";

    public static IReadOnlyList<string> All { get; } = [Mrz, Viz];
}

public static class ZonedFieldZones
{
    public const string Mrz = "mrz";
}
