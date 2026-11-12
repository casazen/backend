using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The facts about an owner that a row of the file gives, or that are stored, in the form they are compared and stored in (upper case and no
/// spaces for the identifiers, the country and the currency in ISO). <c>null</c> = not given. A class: it holds identifiers and bank data, and
/// nothing prints a class that does not say how.
/// </summary>
internal sealed class OwnerValues
{
    public string? Key { get; set; }

    public string? LegalName { get; set; }

    public string? Email { get; set; }

    public OwnerEntityKind? Kind { get; set; }

    public string? TaxResidence { get; set; }

    public string? FiscalCode { get; set; }

    public string? Vat { get; set; }

    public string? Street { get; set; }

    public string? Civic { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Province { get; set; }

    public string? Country { get; set; }

    public string? Currency { get; set; }

    public string? Iban { get; set; }

    public string? Bic { get; set; }
}

/// <summary>One distinct owner of the file: an owner of the registry that rows name, or one that the file would create.</summary>
internal sealed class OwnerPlan(Guid id, PropertyOwner? existing)
{
    /// <summary>The id of the owner; for one the file would create, the id it will have.</summary>
    public Guid Id { get; } = id;

    /// <summary>The owner as it is stored (no tracking); <c>null</c> for one the file would create.</summary>
    public PropertyOwner? Existing { get; } = existing;

    public bool IsNew => Existing is null;

    /// <summary>The identity the file gave this owner so far (a new owner is found again by it in a later row).</summary>
    public OwnerValues Given { get; } = new();

    /// <summary>The first row that named it: the one that creates it.</summary>
    public int FirstRow { get; set; }
}

/// <summary>A row of the file while it is judged and applied.</summary>
internal sealed class OwnerRowState(ParsedOwnerRow source)
{
    public ParsedOwnerRow Source { get; } = source;

    public List<BulkImportRowError> Errors { get; } = [];

    /// <summary>What the row names the property by, clipped (the id as written, or the CIN normalized).</summary>
    public string? PropertyRef { get; set; }

    public Guid? PropertyId { get; set; }

    public OwnerValues Values { get; } = new();

    public bool IsOwnershipRow { get; set; }

    public decimal Share { get; set; }

    public DateOnly Start { get; set; }

    public DateOnly? End { get; set; }

    public PropertyOwner? ExistingOwner { get; set; }

    public string? MatchedBy { get; set; }

    public List<PropertyOwner> Candidates { get; } = [];

    public OwnerPlan? Plan { get; set; }

    public string? OwnerAction { get; set; }

    public string? OwnershipAction { get; set; }

    /// <summary>The stored period this row is the same period as (same property, owner and first day).</summary>
    public Guid? ExistingPeriodId { get; set; }

    public bool Applied { get; set; }

    public bool Valid => Errors.Count == 0;

    public void Fail(string column, string code)
    {
        if (!Errors.Any(e => e.Column == column && e.Code == code))
            Errors.Add(new BulkImportRowError(column, code));
    }
}

/// <summary>The rules of one row of the owner file that need no database: what a cell means, and how an owner's values merge and conflict.</summary>
internal static class OwnerRowRules
{
    private const string KeySymbols = "._:/#-";

    /// <summary>
    /// Reads the cells that describe the owner and the ownership into <see cref="OwnerRowState.Values"/> and the ownership fields, and fails the
    /// ones that are not valid. The fiscal code and the VAT number are only normalized here: whether they are valid depends on the residence,
    /// which may come from the stored owner (<see cref="ValidateIdentifiers"/>).
    /// </summary>
    public static void ParseValues(OwnerRowState state)
    {
        var row = state.Source;
        var values = state.Values;

        if (row.OwnerKey is not null)
        {
            var key = row.OwnerKey.ToUpperInvariant();
            if (IsKey(key))
                values.Key = key;
            else
                state.Fail(OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerKeyInvalid);
        }

        if (row.LegalName is not null)
        {
            if (row.LegalName.Length <= PropertyOwner.LegalNameMaxLength && !row.LegalName.Any(char.IsControl))
                values.LegalName = row.LegalName;
            else
                state.Fail(OwnerImportColumns.LegalName, OwnerImportRowCodes.LegalNameInvalid);
        }

        if (row.Email is not null)
        {
            if (OwnerFieldRules.IsValidEmail(row.Email))
                values.Email = row.Email;
            else
                state.Fail(OwnerImportColumns.Email, OwnerImportRowCodes.EmailInvalid);
        }

        if (row.EntityKind is not null)
        {
            if (OwnerFieldRules.TryParseKind(row.EntityKind, out var kind))
                values.Kind = kind;
            else
                state.Fail(OwnerImportColumns.EntityKind, OwnerImportRowCodes.EntityKindInvalid);
        }

        if (row.TaxResidence is not null)
        {
            if (OwnerFieldRules.TryNormalizeCountry(row.TaxResidence, out var residence))
                values.TaxResidence = residence;
            else
                state.Fail(OwnerImportColumns.TaxResidence, OwnerImportRowCodes.TaxResidenceInvalid);
        }

        if (row.FiscalCode is not null)
            values.FiscalCode = OwnerFieldRules.NormalizeFiscalCode(row.FiscalCode);

        if (row.VatNumber is not null)
            values.Vat = OwnerFieldRules.NormalizeVat(row.VatNumber);

        Address(state, row.BillingStreet, 200, OwnerImportColumns.BillingStreet, v => values.Street = v);
        Address(state, row.BillingCivic, 20, OwnerImportColumns.BillingCivic, v => values.Civic = v);
        Address(state, row.BillingPostalCode, 20, OwnerImportColumns.BillingPostalCode, v => values.PostalCode = v);
        Address(state, row.BillingCity, 100, OwnerImportColumns.BillingCity, v => values.City = v);
        Address(state, row.BillingProvince, 50, OwnerImportColumns.BillingProvince, v => values.Province = v);
        if (row.BillingCountry is not null)
        {
            if (OwnerFieldRules.TryNormalizeCountry(row.BillingCountry, out var country))
                values.Country = country;
            else
                state.Fail(OwnerImportColumns.BillingCountry, OwnerImportRowCodes.AddressInvalid);
        }

        if (row.SettlementCurrency is not null)
        {
            if (OwnerFieldRules.TryNormalizeCurrency(row.SettlementCurrency, out var currency))
                values.Currency = currency;
            else
                state.Fail(OwnerImportColumns.SettlementCurrency, OwnerImportRowCodes.CurrencyInvalid);
        }

        if (row.Iban is not null)
        {
            var iban = OwnerFieldRules.NormalizeIban(row.Iban);
            if (OwnerFieldRules.IsValidIban(iban))
                values.Iban = iban;
            else
                state.Fail(OwnerImportColumns.Iban, OwnerImportRowCodes.IbanInvalid);
        }

        if (row.Bic is not null)
        {
            var bic = OwnerFieldRules.NormalizeBic(row.Bic);
            if (OwnerFieldRules.IsValidBic(bic))
                values.Bic = bic;
            else
                state.Fail(OwnerImportColumns.Bic, OwnerImportRowCodes.BicInvalid);
        }

        ParseOwnership(state);
    }

    /// <summary>
    /// The fiscal code and the VAT number against the residence they are judged under: the residence of the row, else the one the stored owner
    /// has, else Italy. An identifier that is not valid is failed (and dropped, so it cannot identify or be stored).
    /// </summary>
    public static void ValidateIdentifiers(OwnerRowState state, string? residence)
    {
        var values = state.Values;
        if (values.FiscalCode is { } fiscal && !OwnerFieldRules.IsValidFiscalId(fiscal, residence))
        {
            state.Fail(OwnerImportColumns.FiscalCode, OwnerImportRowCodes.FiscalCodeInvalid);
            values.FiscalCode = null;
        }

        if (values.Vat is { } vat && !OwnerFieldRules.IsValidVat(vat, residence))
        {
            state.Fail(OwnerImportColumns.VatNumber, OwnerImportRowCodes.VatNumberInvalid);
            values.Vat = null;
        }
    }

    /// <summary>The values a stored owner holds, in the form they are compared in; what is empty is <c>null</c>.</summary>
    public static OwnerValues ValuesOf(PropertyOwner owner) => new()
    {
        Key = Blank(owner.ExternalKey),
        LegalName = Blank(owner.LegalName),
        Email = Blank(owner.Email),
        Kind = owner.EntityKind,
        TaxResidence = Blank(owner.TaxResidenceCountry),
        FiscalCode = Blank(OwnerFieldRules.NormalizeFiscalCode(owner.FiscalCode)),
        Vat = Blank(OwnerFieldRules.NormalizeVat(owner.VatNumber)),
        Street = Blank(owner.BillingStreet),
        Civic = Blank(owner.BillingCivic),
        PostalCode = Blank(owner.BillingPostalCode),
        City = Blank(owner.BillingCity),
        Province = Blank(owner.BillingProvince),
        Country = Blank(owner.BillingCountry),
        Currency = Blank(owner.SettlementCurrency),
        Iban = Blank(OwnerFieldRules.NormalizeIban(owner.Iban)),
        Bic = Blank(OwnerFieldRules.NormalizeBic(owner.Bic)),
    };

    /// <summary>
    /// What <paramref name="given"/> says that differs from <paramref name="held"/> (both non-empty and not equal): the import fills what is
    /// empty and never overwrites a value. The identifiers are conflicts of identity, the rest conflicts of a field.
    /// </summary>
    public static IEnumerable<(string Column, string Code)> Conflicts(OwnerValues held, OwnerValues given)
    {
        if (Differs(held.Key, given.Key))
            yield return (OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerIdentityConflict);

        if (Differs(held.FiscalCode, given.FiscalCode))
            yield return (OwnerImportColumns.FiscalCode, OwnerImportRowCodes.OwnerIdentityConflict);

        if (Differs(held.Vat, given.Vat))
            yield return (OwnerImportColumns.VatNumber, OwnerImportRowCodes.OwnerIdentityConflict);

        if (Differs(held.LegalName, given.LegalName))
            yield return (OwnerImportColumns.LegalName, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Email, given.Email))
            yield return (OwnerImportColumns.Email, OwnerImportRowCodes.OwnerFieldConflict);

        if (held.Kind is { } heldKind && given.Kind is { } givenKind && heldKind != givenKind)
            yield return (OwnerImportColumns.EntityKind, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.TaxResidence, given.TaxResidence))
            yield return (OwnerImportColumns.TaxResidence, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Street, given.Street))
            yield return (OwnerImportColumns.BillingStreet, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Civic, given.Civic))
            yield return (OwnerImportColumns.BillingCivic, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.PostalCode, given.PostalCode))
            yield return (OwnerImportColumns.BillingPostalCode, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.City, given.City))
            yield return (OwnerImportColumns.BillingCity, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Province, given.Province))
            yield return (OwnerImportColumns.BillingProvince, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Country, given.Country))
            yield return (OwnerImportColumns.BillingCountry, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Currency, given.Currency))
            yield return (OwnerImportColumns.SettlementCurrency, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Iban, given.Iban))
            yield return (OwnerImportColumns.Iban, OwnerImportRowCodes.OwnerFieldConflict);

        if (Differs(held.Bic, given.Bic))
            yield return (OwnerImportColumns.Bic, OwnerImportRowCodes.OwnerFieldConflict);
    }

    /// <summary>Adds to <paramref name="target"/> what <paramref name="given"/> says and it does not hold yet; true when something was added.</summary>
    public static bool Fill(OwnerValues target, OwnerValues given)
    {
        var changed = false;
        changed |= FillText(target.Key, given.Key, v => target.Key = v);
        changed |= FillText(target.LegalName, given.LegalName, v => target.LegalName = v);
        changed |= FillText(target.Email, given.Email, v => target.Email = v);
        changed |= FillText(target.TaxResidence, given.TaxResidence, v => target.TaxResidence = v);
        changed |= FillText(target.FiscalCode, given.FiscalCode, v => target.FiscalCode = v);
        changed |= FillText(target.Vat, given.Vat, v => target.Vat = v);
        changed |= FillText(target.Street, given.Street, v => target.Street = v);
        changed |= FillText(target.Civic, given.Civic, v => target.Civic = v);
        changed |= FillText(target.PostalCode, given.PostalCode, v => target.PostalCode = v);
        changed |= FillText(target.City, given.City, v => target.City = v);
        changed |= FillText(target.Province, given.Province, v => target.Province = v);
        changed |= FillText(target.Country, given.Country, v => target.Country = v);
        changed |= FillText(target.Currency, given.Currency, v => target.Currency = v);
        changed |= FillText(target.Iban, given.Iban, v => target.Iban = v);
        changed |= FillText(target.Bic, given.Bic, v => target.Bic = v);
        if (target.Kind is null && given.Kind is { } kind)
        {
            target.Kind = kind;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Writes into <paramref name="owner"/> what <paramref name="values"/> holds and the owner does not hold yet (nothing is overwritten); true
    /// when a field was written. For an owner the import creates, the first and the last name are made from the legal name, as a display name.
    /// </summary>
    public static bool FillEntity(PropertyOwner owner, OwnerValues values, bool creating)
    {
        var changed = false;
        changed |= FillColumn(owner.ExternalKey, values.Key, v => owner.ExternalKey = v);
        changed |= FillColumn(owner.LegalName, values.LegalName, v => owner.LegalName = v);
        changed |= FillColumn(owner.Email, values.Email, v => owner.Email = v);
        changed |= FillColumn(owner.TaxResidenceCountry, values.TaxResidence, v => owner.TaxResidenceCountry = v);
        changed |= FillColumn(owner.FiscalCode, values.FiscalCode, v => owner.FiscalCode = v);
        changed |= FillColumn(owner.VatNumber, values.Vat, v => owner.VatNumber = v);
        changed |= FillColumn(owner.BillingStreet, values.Street, v => owner.BillingStreet = v);
        changed |= FillColumn(owner.BillingCivic, values.Civic, v => owner.BillingCivic = v);
        changed |= FillColumn(owner.BillingPostalCode, values.PostalCode, v => owner.BillingPostalCode = v);
        changed |= FillColumn(owner.BillingCity, values.City, v => owner.BillingCity = v);
        changed |= FillColumn(owner.BillingProvince, values.Province, v => owner.BillingProvince = v);
        changed |= FillColumn(owner.BillingCountry, values.Country, v => owner.BillingCountry = v);
        changed |= FillColumn(owner.SettlementCurrency, values.Currency, v => owner.SettlementCurrency = v);
        changed |= FillColumn(owner.Iban, values.Iban, v => owner.Iban = v);
        changed |= FillColumn(owner.Bic, values.Bic, v => owner.Bic = v);
        if (owner.EntityKind is null && values.Kind is { } kind)
        {
            owner.EntityKind = kind;
            changed = true;
        }

        if (creating && values.LegalName is { } legalName)
        {
            var (first, last) = OwnerFieldRules.SplitName(legalName, values.Kind ?? owner.EntityKind ?? OwnerEntityKind.NaturalPerson);
            owner.FirstName = first;
            owner.LastName = last;
        }

        return changed;
    }

    private static void ParseOwnership(OwnerRowState state)
    {
        var row = state.Source;
        if (!row.NamesProperty)
        {
            // A share or a day with no property to put it on.
            if (row.HasOwnershipFacts)
                state.Fail(row.PropertyId is not null || row.Cin is null ? OwnerImportColumns.PropertyId : OwnerImportColumns.CinCode, OwnerImportRowCodes.PropertyNotFound);

            return;
        }

        state.IsOwnershipRow = true;
        if (OwnerImportParser.TryParseShare(row.SharePercent, out var share))
            state.Share = share;
        else
            state.Fail(OwnerImportColumns.SharePercent, OwnerImportRowCodes.ShareInvalid);

        var startKnown = false;
        if (row.OwnershipStart is null)
        {
            state.Fail(OwnerImportColumns.OwnershipStart, OwnerImportRowCodes.OwnershipStartRequired);
        }
        else if (OwnerImportParser.TryParseDate(row.OwnershipStart, out var start))
        {
            state.Start = start;
            startKnown = true;
        }
        else
        {
            state.Fail(OwnerImportColumns.OwnershipStart, OwnerImportRowCodes.DateInvalid);
        }

        if (row.OwnershipEnd is null)
            return;

        if (!OwnerImportParser.TryParseDate(row.OwnershipEnd, out var end))
        {
            state.Fail(OwnerImportColumns.OwnershipEnd, OwnerImportRowCodes.DateInvalid);
        }
        else if (startKnown && end < state.Start)
        {
            state.Fail(OwnerImportColumns.OwnershipEnd, OwnerImportRowCodes.PeriodInvalid);
        }
        else
        {
            state.End = end;
        }
    }

    private static void Address(OwnerRowState state, string? text, int maxLength, string column, Action<string> set)
    {
        if (text is null)
            return;

        if (text.Length <= maxLength && !text.Any(char.IsControl))
            set(text);
        else
            state.Fail(column, OwnerImportRowCodes.AddressInvalid);
    }

    /// <summary>1 to 64 letters, digits and <c>. _ : / # -</c>, starting with a letter or a digit: an agency code such as <c>PRO-0042</c>.</summary>
    private static bool IsKey(string key) =>
        key.Length is >= 1 and <= PropertyOwner.ExternalKeyMaxLength
        && char.IsAsciiLetterOrDigit(key[0])
        && key.All(c => char.IsAsciiLetterOrDigit(c) || KeySymbols.Contains(c));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Differs(string? held, string? given) =>
        held is not null && given is not null && !string.Equals(held.Trim(), given.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool FillText(string? held, string? given, Action<string> set)
    {
        if (held is not null || given is null)
            return false;

        set(given);
        return true;
    }

    private static bool FillColumn(string? held, string? given, Action<string> set)
    {
        if (!string.IsNullOrWhiteSpace(held) || given is null)
            return false;

        set(given);
        return true;
    }
}
