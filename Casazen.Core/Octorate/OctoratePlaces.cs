using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Octorate;

/// <summary>
/// One entry of the table of Italian places that Octorate keeps for the check-in (<c>GET /rest/v2/checkin/city/IT</c>, sandbox 2026-11-04: 11,149
/// entries): the exact name Octorate recognizes (<c>internalName</c>) and the police code of the place (<c>countryPoliceCode</c>, the 9-character code
/// of the Alloggiati "Comuni" table CasaZen keeps on a guest).
/// </summary>
/// <param name="Name">The name as Octorate writes it.</param>
/// <param name="PoliceCode">The police code of the place.</param>
/// <param name="Type">What the entry is (<c>regionType</c>, <c>CITY</c> in the sandbox); <c>null</c> when the answer does not say.</param>
public sealed record OctoratePlaceEntry(string Name, string PoliceCode, string? Type);

/// <summary>
/// Octorate's table of places, ready to be asked: the exact name of a police code, or of a name typed by a guest when exactly one place carries it.
/// Pure and immutable: it is built once from the answer of the client and shared (cached) by every send.
/// </summary>
public sealed class OctoratePlaceTable
{
    private readonly Dictionary<string, string> _nameByCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _codesByName = new(StringComparer.Ordinal);

    /// <summary>Builds the table. An entry with no name or no code is left out; the first name of a code stands.</summary>
    public OctoratePlaceTable(IEnumerable<OctoratePlaceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            var code = AlloggiatiRecordRules.NormalizeCode(entry.PoliceCode);
            var name = entry.Name?.Trim();
            if (code is null || string.IsNullOrEmpty(name))
                continue;

            _nameByCode.TryAdd(code, name);
            var key = AlloggiatiRecordRules.NormalizeDescription(name);
            if (key.Length == 0)
                continue;

            if (!_codesByName.TryGetValue(key, out var codes))
                _codesByName[key] = codes = new HashSet<string>(StringComparer.Ordinal);
            codes.Add(code);
        }
    }

    /// <summary>How many places the table holds.</summary>
    public int Count => _nameByCode.Count;

    /// <summary>The exact name Octorate uses for a police code; <c>null</c> when the table has no such place.</summary>
    public string? NameOf(string? policeCode) =>
        AlloggiatiRecordRules.NormalizeCode(policeCode) is { } code && _nameByCode.TryGetValue(code, out var name) ? name : null;

    /// <summary>
    /// The exact name of the place a guest typed, for a guest who has no code: the one place whose name matches it (case, accents, spaces and signs
    /// ignored). <c>null</c> when none matches or when two different places carry the same name: a name that is not unique is never guessed.
    /// </summary>
    public string? NameOfUnique(string? typedName)
    {
        var key = AlloggiatiRecordRules.NormalizeDescription(typedName);
        if (key.Length == 0 || !_codesByName.TryGetValue(key, out var codes) || codes.Count != 1)
            return null;

        return _nameByCode[codes.Single()];
    }
}

/// <summary>
/// The places of a guest as Octorate recognizes them (decision O2): the exact names to send, and the fields whose place Octorate's table does not know.
/// A guest with an unrecognized field is not sent: the host corrects the place first.
/// </summary>
/// <param name="BirthCity">The comune of birth, for a guest born in Italy.</param>
/// <param name="ResidenceCity">The comune of residence, for a guest living in Italy.</param>
/// <param name="DocumentIssuePlace">The place of issue of the document, for a guest who carries one.</param>
/// <param name="UnrecognizedFields">The camelCase names of the fields (<see cref="OctoratePlaceRules.BirthComuneField"/>, ...) whose place has no match.</param>
public sealed record OctorateGuestPlaces(
    string? BirthCity,
    string? ResidenceCity,
    string? DocumentIssuePlace,
    IReadOnlyList<string> UnrecognizedFields)
{
    /// <summary>Every place the guest needs is known to Octorate.</summary>
    public bool IsRecognized => UnrecognizedFields.Count == 0;

    /// <summary>The unrecognized fields as the link keeps them (comma separated); <c>null</c> when every place is known.</summary>
    public string? FieldsText => IsRecognized ? null : string.Join(',', UnrecognizedFields);
}

/// <summary>
/// How the places of a guest become the names Octorate recognizes (E20/F01/US01/T01, decision O2), pure: no call, no database. The sandbox accepted a
/// complete guest only when <c>city</c> and the other places were names of its own table (it then returns the police codes), so CasaZen maps the
/// police codes it already holds on the guest to those names. Only the Italian comuni are mapped: Octorate has no table of states that is known to
/// exist, so a state, a citizenship and the country of residence stay as they were sent (a state is never a reason to hold a guest back).
/// </summary>
public static class OctoratePlaceRules
{
    /// <summary>The stable code of a guest that was not sent because one of its places is not in Octorate's table.</summary>
    public const string PlaceNotRecognizedCode = "place_not_recognized";

    /// <summary>The field of the comune of birth (the name of the Alloggiati record field).</summary>
    public const string BirthComuneField = AlloggiatiRecordRules.FieldBirthComune;

    /// <summary>The field of the comune of residence (the name the check-in readiness rules use).</summary>
    public const string ResidenceComuneField = CheckInReadinessRules.FieldResidenceComune;

    /// <summary>The field of the place of issue of the document (the name of the Alloggiati record field).</summary>
    public const string DocumentIssuePlaceField = AlloggiatiRecordRules.FieldDocumentIssuePlace;

    /// <summary>
    /// The prefix of the codes of the Alloggiati "Stati" table (<c>100000100</c> is Italy): a place of issue with such a code is a state, which has no
    /// entry in the table of comuni and is not looked up.
    /// </summary>
    private const string StateCodePrefix = "100";

    /// <summary>
    /// True when the guest has a place that has to be found in the table of Italian places: a comune of birth or residence, or a place of issue whose
    /// code is a comune's. A guest with none (a foreign guest with a foreign document) never needs the table, so it never waits for it.
    /// </summary>
    public static bool NeedsItalianPlaces(StayGuest guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        return guest.BornInItaly == true
               || guest.ResidenceInItaly == true
               || (AlloggiatiRecordRules.RequiresDocument(guest.Type) && IssuedInItalyByCode(guest));
    }

    /// <summary>
    /// The places of <paramref name="guest"/> in Octorate's names. A comune of birth or residence has to be found, by its police code or, when the guest
    /// has none, by its name when exactly one place carries it; the place of issue of a document is looked up the same way but a name that does not
    /// match is kept as it was typed (it may be a state). Anything else is not touched.
    /// </summary>
    public static OctorateGuestPlaces Resolve(StayGuest guest, OctoratePlaceTable italianPlaces)
    {
        ArgumentNullException.ThrowIfNull(guest);
        ArgumentNullException.ThrowIfNull(italianPlaces);
        var unrecognized = new List<string>(3);

        string? birth = null;
        if (guest.BornInItaly == true)
        {
            birth = Find(italianPlaces, guest.BirthComuneCode, guest.BirthComuneName);
            if (birth is null)
                unrecognized.Add(BirthComuneField);
        }

        string? residence = null;
        if (guest.ResidenceInItaly == true)
        {
            residence = Find(italianPlaces, guest.ResidenceComuneCode, guest.ResidenceComuneName);
            if (residence is null)
                unrecognized.Add(ResidenceComuneField);
        }

        string? issuePlace = null;
        if (AlloggiatiRecordRules.RequiresDocument(guest.Type))
        {
            var code = AlloggiatiRecordRules.NormalizeCode(guest.DocumentIssuePlaceCode);
            issuePlace = Find(italianPlaces, code, guest.DocumentIssuePlaceName);
            if (issuePlace is null && code is not null && !IsStateCode(code))
                unrecognized.Add(DocumentIssuePlaceField);
            else
                issuePlace ??= NullIfBlank(guest.DocumentIssuePlaceName);
        }

        return new OctorateGuestPlaces(birth, residence, issuePlace, unrecognized);
    }

    /// <summary>The name by the police code when the guest has one, otherwise by the typed name (a code that is not in the table is not recovered by the name).</summary>
    private static string? Find(OctoratePlaceTable table, string? code, string? typedName) =>
        AlloggiatiRecordRules.NormalizeCode(code) is { } known ? table.NameOf(known) : table.NameOfUnique(typedName);

    private static bool IssuedInItalyByCode(StayGuest guest) =>
        AlloggiatiRecordRules.NormalizeCode(guest.DocumentIssuePlaceCode) is { } code && !IsStateCode(code);

    private static bool IsStateCode(string code) => code.StartsWith(StateCodePrefix, StringComparison.Ordinal);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
