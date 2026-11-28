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

    public static AlloggiatiCodeBook Empty { get; } =
        new(new Dictionary<AlloggiatiCodeTable, int>(), []);

    public bool IsLoaded(AlloggiatiCodeTable table) => _rowCounts.TryGetValue(table, out var rows) && rows > 0;

    public AlloggiatiCodeEntry? FindCode(AlloggiatiCodeTable table, string? code) =>
        AlloggiatiRecordRules.NormalizeCode(code) is { } normalized && _byCode.TryGetValue((table, normalized), out var entry)
            ? entry
            : null;

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
            ? Resolve([AlloggiatiCodeTable.Documenti], guest.DocumentTypeCode, null)
            : ResolvedCode.NotRequired;
        var issuePlace = requiresDocument
            ? Resolve(PlaceTables, guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName)
            : ResolvedCode.NotRequired;

        return new StayGuestCodes(type, birthComune, birthCountry, citizenship, documentType, issuePlace);
    }

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
            if (ItalianCitizenshipNames.OfficialNormalizedKey(guest.CitizenshipName) is { } citizenshipCountry)
                keys[AlloggiatiCodeTable.Stati].Names.Add(citizenshipCountry);
            keys[AlloggiatiCodeTable.Documenti].Add(guest.DocumentTypeCode, null);
            keys[AlloggiatiCodeTable.Comuni].Add(guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName);
            keys[AlloggiatiCodeTable.Stati].Add(guest.DocumentIssuePlaceCode, guest.DocumentIssuePlaceName);

            keys[AlloggiatiCodeTable.Comuni].Add(guest.ResidenceComuneCode, guest.ResidenceComuneName);
            keys[AlloggiatiCodeTable.Stati].Add(guest.ResidenceCountryCode, guest.ResidenceCountryName);
        }

        return keys;
    }
}

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

public enum CodeResolutionStatus
{
    Resolved,

    NotRequired,

    TableEmpty,

    NotFound,

    Ambiguous,
}

public sealed record ResolvedCode(string? Code, string? Description, CodeResolutionStatus Status)
{
    public static ResolvedCode NotRequired { get; } = new(null, null, CodeResolutionStatus.NotRequired);
    public static ResolvedCode TableEmpty { get; } = new(null, null, CodeResolutionStatus.TableEmpty);
    public static ResolvedCode NotFound { get; } = new(null, null, CodeResolutionStatus.NotFound);
    public static ResolvedCode Ambiguous { get; } = new(null, null, CodeResolutionStatus.Ambiguous);

    public static ResolvedCode Found(AlloggiatiCodeEntry entry) => new(entry.Code, entry.Description, CodeResolutionStatus.Resolved);

    public bool IsComplete => Status is CodeResolutionStatus.Resolved or CodeResolutionStatus.NotRequired;
}

public sealed record StayGuestCodes(
    ResolvedCode Type,
    ResolvedCode BirthComune,
    ResolvedCode BirthCountry,
    ResolvedCode Citizenship,
    ResolvedCode DocumentType,
    ResolvedCode DocumentIssuePlace)
{
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
