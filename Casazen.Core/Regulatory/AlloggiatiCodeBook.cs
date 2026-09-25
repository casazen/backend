using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// The codes of the official Alloggiati tables needed by some guests, as loaded from the imported tables
/// (<see cref="AlloggiatiCodeEntry"/>). Resolves each coded field of a guest: by the stored code when there is one, or
/// else by a <b>unique</b> match of the entered name against the official description (for comuni also the province).
/// Nothing else: an empty table, an unknown code or an ambiguous name leaves the code "to complete", which blocks only the
/// export of the record (CO-12).
/// </summary>
public sealed class AlloggiatiCodeBook
{
    private static readonly AlloggiatiCodeTable[] PlaceTables = [AlloggiatiCodeTable.Comuni, AlloggiatiCodeTable.Stati];

    private readonly IReadOnlyDictionary<AlloggiatiCodeTable, int> _rowCounts;
    private readonly Dictionary<(AlloggiatiCodeTable Table, string Code), AlloggiatiCodeEntry> _byCode = new();
    private readonly Dictionary<(AlloggiatiCodeTable Table, string Name), List<AlloggiatiCodeEntry>> _byName = new();

    /// <param name="rowCounts">Rows of each imported table (a table with no rows is not loaded).</param>
    /// <param name="entries">The entries of interest (usually those matching <see cref="KeysOf"/>).</param>
    public AlloggiatiCodeBook(IReadOnlyDictionary<AlloggiatiCodeTable, int> rowCounts, IEnumerable<AlloggiatiCodeEntry> entries)
    {
        _rowCounts = rowCounts;
        foreach (var entry in entries)
        {
            _byCode[(entry.Table, entry.Code)] = entry;
            var key = (entry.Table, entry.NormalizedDescription);
            if (!_byName.TryGetValue(key, out var list))
                _byName[key] = list = [];
            list.Add(entry);
        }
    }

    /// <summary>No table imported: every code is to complete.</summary>
    public static AlloggiatiCodeBook Empty { get; } =
        new(new Dictionary<AlloggiatiCodeTable, int>(), []);

    public bool IsLoaded(AlloggiatiCodeTable table) => _rowCounts.TryGetValue(table, out var rows) && rows > 0;

    /// <summary>The entry with this code in <paramref name="table"/>, if imported.</summary>
    public AlloggiatiCodeEntry? FindCode(AlloggiatiCodeTable table, string? code) =>
        AlloggiatiRecordRules.NormalizeCode(code) is { } normalized && _byCode.TryGetValue((table, normalized), out var entry)
            ? entry
            : null;

    /// <summary>
    /// Code of a value in one of <paramref name="tables"/>: the stored <paramref name="code"/> if it exists there, or else
    /// the only entry whose description matches <paramref name="name"/> (and <paramref name="province"/> for comuni).
    /// </summary>
    public ResolvedCode Resolve(IReadOnlyList<AlloggiatiCodeTable> tables, string? code, string? name, string? province = null)
    {
        if (!tables.Any(IsLoaded))
            return ResolvedCode.TableEmpty;

        if (AlloggiatiRecordRules.NormalizeCode(code) is { } normalizedCode)
        {
            foreach (var table in tables)
            {
                if (_byCode.TryGetValue((table, normalizedCode), out var entry))
                    return ResolvedCode.Found(entry);
            }

            return ResolvedCode.NotFound;
        }

        var key = AlloggiatiRecordRules.NormalizeDescription(name);
        if (key.Length == 0)
            return ResolvedCode.NotFound;

        var matches = tables
            .SelectMany(table => _byName.TryGetValue((table, key), out var list) ? list : [])
            .Where(entry => province is null || entry.Province is null || entry.Province == province)
            .ToList();

        return matches.Count switch
        {
            1 => ResolvedCode.Found(matches[0]),
            0 => ResolvedCode.NotFound,
            _ => ResolvedCode.Ambiguous,
        };
    }

    /// <summary>Codes of every coded field of the guest's record line.</summary>
    public StayGuestCodes ResolveGuest(StayGuest guest)
    {
        var type = Resolve([AlloggiatiCodeTable.TipiAlloggiato], null, AlloggiatiRecordRules.OfficialNormalizedName(guest.Type));

        var birthComune = guest.BornInItaly == true
            ? Resolve([AlloggiatiCodeTable.Comuni], guest.BirthComuneCode, guest.BirthComuneName, AlloggiatiRecordRules.NormalizeCode(guest.BirthProvince))
            : ResolvedCode.NotRequired;

        var birthCountry = guest.BornInItaly switch
        {
            true => Resolve([AlloggiatiCodeTable.Stati], guest.BirthCountryCode, AlloggiatiRecordRules.ItalyNormalizedDescription),
            false => Resolve([AlloggiatiCodeTable.Stati], guest.BirthCountryCode, guest.BirthCountryName),
            null => IsLoaded(AlloggiatiCodeTable.Stati) ? ResolvedCode.NotFound : ResolvedCode.TableEmpty,
        };

        var citizenship = Resolve([AlloggiatiCodeTable.Stati], guest.CitizenshipCode, guest.CitizenshipName);

        var requiresDocument = AlloggiatiRecordRules.RequiresDocument(guest.Type);
        var documentType = requiresDocument
            // Only the stored code counts: the document kinds of CasaZen have no official description to match.
            ? Resolve([AlloggiatiCodeTable.Documenti], guest.DocumentTypeCode, null)
            : ResolvedCode.NotRequired;
        var issuePlace = requiresDocument
            ? Resolve(PlaceTables, guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName)
            : ResolvedCode.NotRequired;

        return new StayGuestCodes(type, birthComune, birthCountry, citizenship, documentType, issuePlace);
    }

    /// <summary>Codes and normalized names to load from each table to resolve <paramref name="guests"/>.</summary>
    public static IReadOnlyDictionary<AlloggiatiCodeTable, CodeBookKeys> KeysOf(IEnumerable<StayGuest> guests)
    {
        var keys = Enum.GetValues<AlloggiatiCodeTable>().ToDictionary(t => t, _ => new CodeBookKeys());
        foreach (var type in Enum.GetValues<StayGuestType>())
            keys[AlloggiatiCodeTable.TipiAlloggiato].Names.Add(AlloggiatiRecordRules.OfficialNormalizedName(type));
        keys[AlloggiatiCodeTable.Stati].Names.Add(AlloggiatiRecordRules.ItalyNormalizedDescription);

        foreach (var guest in guests)
        {
            keys[AlloggiatiCodeTable.Comuni].Add(guest.BirthComuneCode, guest.BirthComuneName);
            keys[AlloggiatiCodeTable.Stati].Add(guest.BirthCountryCode, guest.BirthCountryName);
            keys[AlloggiatiCodeTable.Stati].Add(guest.CitizenshipCode, guest.CitizenshipName);
            keys[AlloggiatiCodeTable.Documenti].Add(guest.DocumentTypeCode, null);
            keys[AlloggiatiCodeTable.Comuni].Add(guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName);
            keys[AlloggiatiCodeTable.Stati].Add(guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName);
        }

        return keys;
    }
}

/// <summary>Codes and normalized descriptions to look up in one table.</summary>
public sealed class CodeBookKeys
{
    public HashSet<string> Codes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    public void Add(string? code, string? name)
    {
        if (AlloggiatiRecordRules.NormalizeCode(code) is { } normalized)
            Codes.Add(normalized);
        var key = AlloggiatiRecordRules.NormalizeDescription(name);
        if (key.Length > 0)
            Names.Add(key);
    }
}

/// <summary>How the code of a field was found.</summary>
public enum CodeResolutionStatus
{
    /// <summary>Found in the imported official table.</summary>
    Resolved,

    /// <summary>The field is not part of this guest's record line (e.g. document of a family member).</summary>
    NotRequired,

    /// <summary>The official table has not been imported.</summary>
    TableEmpty,

    /// <summary>No entry with the stored code, or no entry with the entered name.</summary>
    NotFound,

    /// <summary>More than one entry matches the entered name: the code must be chosen.</summary>
    Ambiguous,
}

/// <summary>Code of one field of the record, with the official description when found.</summary>
public sealed record ResolvedCode(string? Code, string? Description, CodeResolutionStatus Status)
{
    public static ResolvedCode NotRequired { get; } = new(null, null, CodeResolutionStatus.NotRequired);
    public static ResolvedCode TableEmpty { get; } = new(null, null, CodeResolutionStatus.TableEmpty);
    public static ResolvedCode NotFound { get; } = new(null, null, CodeResolutionStatus.NotFound);
    public static ResolvedCode Ambiguous { get; } = new(null, null, CodeResolutionStatus.Ambiguous);

    public static ResolvedCode Found(AlloggiatiCodeEntry entry) => new(entry.Code, entry.Description, CodeResolutionStatus.Resolved);

    /// <summary>True when the export has what it needs for this field.</summary>
    public bool IsComplete => Status is CodeResolutionStatus.Resolved or CodeResolutionStatus.NotRequired;
}

/// <summary>Codes of the coded fields of one guest's record line.</summary>
public sealed record StayGuestCodes(
    ResolvedCode Type,
    ResolvedCode BirthComune,
    ResolvedCode BirthCountry,
    ResolvedCode Citizenship,
    ResolvedCode DocumentType,
    ResolvedCode DocumentIssuePlace)
{
    /// <summary>Record fields whose code is still to complete ("codice da completare"), as camelCase field names.</summary>
    public IReadOnlyList<string> CodesToComplete
    {
        get
        {
            var fields = new List<string>();
            if (!Type.IsComplete) fields.Add(AlloggiatiRecordRules.FieldType);
            if (!BirthComune.IsComplete) fields.Add(AlloggiatiRecordRules.FieldBirthComune);
            if (!BirthCountry.IsComplete) fields.Add(AlloggiatiRecordRules.FieldBirthCountry);
            if (!Citizenship.IsComplete) fields.Add(AlloggiatiRecordRules.FieldCitizenship);
            if (!DocumentType.IsComplete) fields.Add(AlloggiatiRecordRules.FieldDocumentType);
            if (!DocumentIssuePlace.IsComplete) fields.Add(AlloggiatiRecordRules.FieldDocumentIssuePlace);
            return fields;
        }
    }
}
