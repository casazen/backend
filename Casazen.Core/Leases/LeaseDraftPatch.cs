using System.Text.Json;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Leases;

/// <summary>
/// The request cannot be read as the values of a step: a key the step does not have, a value of the wrong type, a text too long, an id
/// that is not one of the draft. A mistake of the client, not a value the landlord typed wrong (those are kept and reported):
/// answered 400 <c>validation_error</c>. The message names the field and never carries what was sent.
/// </summary>
public sealed class LeaseDraftValuesException(string field, string reason) : Exception($"Draft values rejected at '{field}': {reason}")
{
    public string Field { get; } = field;
}

/// <summary>
/// Applies the <c>values</c> of one step to the values typed so far (LR-12). Merge semantics: a key that is there replaces the
/// value of that field (<c>null</c> clears it, a list is replaced as a whole), a key that is not there leaves it as it was, so a
/// client that sends one field never wipes the others. Nothing is checked for being right, only for being the right shape.
/// </summary>
public static class LeaseDraftPatch
{
    private static readonly string[] Step1Keys = ["propertyId"];
    private static readonly string[] Step2Keys = ["landlords", "tenants"];
    private static readonly string[] Step3Keys =
        ["contractType", "taxRegime", "monthlyRent", "securityDeposit", "concordato", "transitoryParty", "transitoryNeed"];

    private static readonly string[] Step4Keys = ["startDate", "endDate", "transitoryDocumentation"];
    private static readonly string[] Step5Keys = ["parties"];

    private static readonly string[] PartyKeys = ["id", "firstName", "lastName", "fiscalCode", "citizenship", "residence", "contactEmail"];
    private static readonly string[] DocumentsKeys = ["id", "documentsVerifiedOn"];

    private static readonly string[] ConcordatoKeys =
    [
        "sqm", "typeAElementCount", "typeBElementCount", "typeCElementCount", "typeDElementCount", "qualifyingTypeDElementCount",
        "isFurnished", "stoveHeating", "airConditioning", "garageSqm", "balconySqm", "otherAppurtenanceSqm", "privateGreenSqm",
        "zoneName", "cadastralSheet",
    ];

    /// <param name="current">The values typed so far.</param>
    /// <param name="step">1 to 5.</param>
    /// <param name="values">The <c>values</c> of the request: an object.</param>
    /// <param name="newId">Gives the id of a new entry of the parties.</param>
    /// <exception cref="LeaseDraftValuesException">The values are not shaped like the step's.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_parties_too_many</c>: more landlords or tenants than a lease can have.</exception>
    public static LeaseDraftValues Apply(LeaseDraftValues current, int step, JsonElement values, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newId);

        if (step is < 1 or > LeaseDraftDefaults.LastDataStep)
            throw new LeaseDraftValuesException("step", "not a data step");
        if (values.ValueKind != JsonValueKind.Object)
            throw new LeaseDraftValuesException("values", "not an object");

        var properties = Properties(values, step switch { 1 => Step1Keys, 2 => Step2Keys, 3 => Step3Keys, 4 => Step4Keys, _ => Step5Keys }, "values");

        return step switch
        {
            1 => ApplyProperty(current, properties),
            2 => ApplyParties(current, properties, newId),
            3 => ApplyEconomics(current, properties),
            4 => ApplyTerm(current, properties),
            _ => ApplyDocuments(current, properties),
        };
    }

    // ---- step 1 --------------------------------------------------------------------------------------------------

    private static LeaseDraftValues ApplyProperty(LeaseDraftValues current, Dictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue("propertyId", out var value))
            return current;

        // The property is chosen, never un-chosen: a contract has one from the moment it is created.
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty)
            throw new LeaseDraftValuesException("propertyId", "not an id");
        return current with { PropertyId = id };
    }

    // ---- step 2 --------------------------------------------------------------------------------------------------

    private static LeaseDraftValues ApplyParties(LeaseDraftValues current, Dictionary<string, JsonElement> properties, Func<Guid> newId)
    {
        // Every id the draft has, of either role: a new entry never takes one of them.
        var reserved = current.Landlords.Concat(current.Tenants).Select(p => p.Id).ToHashSet();
        var used = new HashSet<Guid>();

        IReadOnlyList<LeaseDraftParty> Entries(string key, IReadOnlyList<LeaseDraftParty> existing)
        {
            if (!properties.TryGetValue(key, out var list))
                return existing;
            if (list.ValueKind == JsonValueKind.Null)
                return [];
            if (list.ValueKind != JsonValueKind.Array)
                throw new LeaseDraftValuesException(key, "not a list");

            // An entry takes again the id of an entry of the SAME role: the id of one of the other role is a new entry, so no id
            // is ever in two entries of the draft (two rows of Parties with one key, a list nobody can save again).
            var known = new Dictionary<Guid, LeaseDraftParty>();
            foreach (var entry in existing)
                known.TryAdd(entry.Id, entry);

            var entries = new List<LeaseDraftParty>();
            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var entry = Party(element, $"{key}[{index++}]", known, reserved, used, newId);
                if (entry is not null)
                    entries.Add(entry);
            }

            if (entries.Count > LeasePartyRules.MaxPerRole)
                throw new DomainRuleException(LeasePartyErrorCodes.TooMany, "LeasePartiesTooMany", LeasePartyRules.MaxPerRole);
            return entries;
        }

        // The entries of the two roles are read one after the other, so an id is used once in the whole request.
        var landlords = Entries("landlords", current.Landlords);
        var tenants = Entries("tenants", current.Tenants);
        return current with { Landlords = landlords, Tenants = tenants };
    }

    private static LeaseDraftParty? Party(
        JsonElement element,
        string path,
        Dictionary<Guid, LeaseDraftParty> known,
        HashSet<Guid> reserved,
        HashSet<Guid> used,
        Func<Guid> newId)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LeaseDraftValuesException(path, "not an object");

        var fields = Properties(element, PartyKeys, path);
        Guid? sentId = null;
        if (fields.TryGetValue("id", out var id) && id.ValueKind != JsonValueKind.Null)
        {
            if (id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var parsed))
                throw new LeaseDraftValuesException($"{path}.id", "not an id");
            sentId = parsed;
        }

        var party = new LeaseDraftParty
        {
            FirstName = Optional(fields, "firstName", path),
            LastName = Optional(fields, "lastName", path),
            FiscalCode = Optional(fields, "fiscalCode", path),
            Citizenship = Optional(fields, "citizenship", path),
            Residence = Optional(fields, "residence", path),
            ContactEmail = Optional(fields, "contactEmail", path),
        };

        // A row of the form nobody typed in is not an entry of the draft.
        if (LeaseDraftRules.IsBlank(party))
            return null;

        // The id the server gave the entry stays with it (and so does what step 5 recorded for it); an id it does not know, one of
        // the other role, or one already used in this request, is a new entry.
        if (sentId is { } existingId && known.TryGetValue(existingId, out var previous) && used.Add(existingId))
            return party with { Id = existingId, DocumentsVerifiedOn = previous.DocumentsVerifiedOn };

        var fresh = newId();
        while (reserved.Contains(fresh) || !used.Add(fresh))
            fresh = newId();
        return party with { Id = fresh };
    }

    // ---- step 3 --------------------------------------------------------------------------------------------------

    private static LeaseDraftValues ApplyEconomics(LeaseDraftValues current, Dictionary<string, JsonElement> properties)
    {
        var result = current;
        if (properties.TryGetValue("contractType", out var type))
            result = result with { ContractType = Text(type, "contractType") };
        if (properties.TryGetValue("taxRegime", out var regime))
            result = result with { TaxRegime = Text(regime, "taxRegime") };
        if (properties.TryGetValue("monthlyRent", out var rent))
            result = result with { MonthlyRent = Number(rent, "monthlyRent") };
        if (properties.TryGetValue("securityDeposit", out var deposit))
            result = result with { SecurityDeposit = Number(deposit, "securityDeposit") };
        if (properties.TryGetValue("concordato", out var concordato))
            result = result with { Concordato = Concordato(concordato) };
        if (properties.TryGetValue("transitoryParty", out var party))
            result = result with { TransitoryParty = Text(party, "transitoryParty") };
        if (properties.TryGetValue("transitoryNeed", out var need))
            result = result with { TransitoryNeed = Text(need, "transitoryNeed") };
        return result;
    }

    private static LeaseDraftConcordato? Concordato(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new LeaseDraftValuesException("concordato", "not an object");

        var fields = Properties(element, ConcordatoKeys, "concordato");
        decimal Decimal(string key) => fields.TryGetValue(key, out var v) ? Number(v, $"concordato.{key}") ?? 0m : 0m;
        int Count(string key) => fields.TryGetValue(key, out var v) ? WholeNumber(v, $"concordato.{key}") : 0;
        bool Flag(string key) => fields.TryGetValue(key, out var v) && ReadBoolean(v, $"concordato.{key}");

        return new LeaseDraftConcordato
        {
            Sqm = Decimal("sqm"),
            TypeAElementCount = Count("typeAElementCount"),
            TypeBElementCount = Count("typeBElementCount"),
            TypeCElementCount = Count("typeCElementCount"),
            TypeDElementCount = Count("typeDElementCount"),
            QualifyingTypeDElementCount = Count("qualifyingTypeDElementCount"),
            IsFurnished = Flag("isFurnished"),
            StoveHeating = Flag("stoveHeating"),
            AirConditioning = Flag("airConditioning"),
            GarageSqm = Decimal("garageSqm"),
            BalconySqm = Decimal("balconySqm"),
            OtherAppurtenanceSqm = Decimal("otherAppurtenanceSqm"),
            PrivateGreenSqm = Decimal("privateGreenSqm"),
            ZoneName = Optional(fields, "zoneName", "concordato"),
            CadastralSheet = Optional(fields, "cadastralSheet", "concordato"),
        };
    }

    // ---- step 4 --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The start date and, for an imported contract or a transitory one, the last day of its first period, and for a transitory one the
    /// documentation of its need. All are kept as typed: the end is read only for those two (<see cref="LeaseDraftRules.Read"/>), so a client
    /// that sends it for a libero or a concordato made in CasaZen changes nothing.
    /// </summary>
    private static LeaseDraftValues ApplyTerm(LeaseDraftValues current, Dictionary<string, JsonElement> properties)
    {
        var result = current;
        if (properties.TryGetValue("startDate", out var start))
            result = result with { StartDate = Text(start, "startDate") };
        if (properties.TryGetValue("endDate", out var end))
            result = result with { EndDate = Text(end, "endDate") };
        if (properties.TryGetValue("transitoryDocumentation", out var documentation))
            result = result with { TransitoryDocumentation = Text(documentation, "transitoryDocumentation") };
        return result;
    }

    // ---- step 5 --------------------------------------------------------------------------------------------------

    private static LeaseDraftValues ApplyDocuments(LeaseDraftValues current, Dictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue("parties", out var list))
            return current;
        if (list.ValueKind == JsonValueKind.Null)
            return current;
        if (list.ValueKind != JsonValueKind.Array)
            throw new LeaseDraftValuesException("parties", "not a list");

        var dates = new Dictionary<Guid, string?>();
        var index = 0;
        foreach (var element in list.EnumerateArray())
        {
            var path = $"parties[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
                throw new LeaseDraftValuesException(path, "not an object");

            var fields = Properties(element, DocumentsKeys, path);
            if (!fields.TryGetValue("id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var partyId))
                throw new LeaseDraftValuesException($"{path}.id", "not an id");
            if (!current.Landlords.Concat(current.Tenants).Any(p => p.Id == partyId))
                throw new LeaseDraftValuesException($"{path}.id", "not a party of the draft");

            if (fields.TryGetValue("documentsVerifiedOn", out var date))
                dates[partyId] = Text(date, $"{path}.documentsVerifiedOn");
        }

        LeaseDraftParty Set(LeaseDraftParty party) => dates.TryGetValue(party.Id, out var date) ? party with { DocumentsVerifiedOn = date } : party;
        return current with
        {
            Landlords = current.Landlords.Select(Set).ToList(),
            Tenants = current.Tenants.Select(Set).ToList(),
        };
    }

    // ---- reading JSON --------------------------------------------------------------------------------------------

    /// <summary>The properties of an object by their canonical name (the name is read without regard to case); a key outside <paramref name="allowed"/> is refused.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement element, string[] allowed, string path)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var canonical = allowed.FirstOrDefault(key => string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new LeaseDraftValuesException($"{path}.{(property.Name.Length > 40 ? "…" : property.Name)}", "not a field of this step");
            if (!result.TryAdd(canonical, property.Value))
                throw new LeaseDraftValuesException($"{path}.{canonical}", "twice");
        }

        return result;
    }

    private static string? Optional(Dictionary<string, JsonElement> fields, string key, string path) =>
        fields.TryGetValue(key, out var value) ? Text(value, $"{path}.{key}") : null;

    private static string? Text(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new LeaseDraftValuesException(path, "not a text");

        var text = value.GetString();
        if (text is { Length: > LeaseDraftRules.MaxTextLength })
            throw new LeaseDraftValuesException(path, "text too long");
        return text;
    }

    private static decimal? Number(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number)
            throw new LeaseDraftValuesException(path, "not a number");

        // A number the type cannot hold (1e30) is a value that is wrong, not a request that is badly made: it is kept as the largest
        // (or the smallest) amount, which no rule accepts, so the flow reports it like any other wrong value and the autosave goes on.
        return value.TryGetDecimal(out var number) ? number : IsNegative(value) ? decimal.MinValue : decimal.MaxValue;
    }

    private static int WholeNumber(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number)
            throw new LeaseDraftValuesException(path, "not a whole number");

        if (value.TryGetInt32(out var number))
            return number;

        // A fraction is not a whole number at all: the request is badly made. A whole number too big for a count (3000000000) is a
        // value that is wrong, kept as an extreme no rule accepts (see Number).
        if (value.TryGetDecimal(out var other) && other != decimal.Truncate(other))
            throw new LeaseDraftValuesException(path, "not a whole number");

        return IsNegative(value) ? int.MinValue : int.MaxValue;
    }

    private static bool IsNegative(JsonElement number) => number.GetRawText().StartsWith('-');

    private static bool ReadBoolean(JsonElement value, string path) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null => false,
        _ => throw new LeaseDraftValuesException(path, "not a boolean"),
    };
}
