using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// One row of data of an owner file, read: each field is the trimmed text of its cell, or <c>null</c> when the column is not in the file or the
/// cell is empty. A class, not a record: it holds the fiscal code, the VAT number, the IBAN and the BIC of the owner for as long as the call
/// lasts, and nothing prints a class that does not say how.
/// </summary>
internal sealed class ParsedOwnerRow(int number)
{
    public int Number { get; } = number;

    public string? PropertyId { get; init; }

    public string? Cin { get; init; }

    public string? OwnerId { get; init; }

    public string? OwnerKey { get; init; }

    public string? LegalName { get; init; }

    public string? Email { get; init; }

    public string? EntityKind { get; init; }

    public string? TaxResidence { get; init; }

    public string? FiscalCode { get; init; }

    public string? VatNumber { get; init; }

    public string? BillingStreet { get; init; }

    public string? BillingCivic { get; init; }

    public string? BillingPostalCode { get; init; }

    public string? BillingCity { get; init; }

    public string? BillingProvince { get; init; }

    public string? BillingCountry { get; init; }

    public string? SharePercent { get; init; }

    public string? OwnershipStart { get; init; }

    public string? OwnershipEnd { get; init; }

    public string? SettlementCurrency { get; init; }

    public string? Iban { get; init; }

    public string? Bic { get; init; }

    public bool NamesProperty => PropertyId is not null || Cin is not null;

    public bool HasOwnershipFacts => SharePercent is not null || OwnershipStart is not null || OwnershipEnd is not null;
}

internal sealed record ParsedOwnerImport(IReadOnlyList<ParsedOwnerRow> Rows, IReadOnlyList<string> UnknownColumns);

internal static class OwnerImportParser
{
    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    public static ParsedOwnerImport Parse(IReadOnlyList<ImportTableRow> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0)
            throw new DomainRuleException(BulkImportCodes.FileEmpty, "OnboardingImportFileEmpty");

        var header = table[0].Cells;
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new List<string>();
        for (var column = 0; column < header.Count; column++)
        {
            var title = header[column].Trim();
            if (title.Length == 0)
                continue;

            if (Aliases.TryGetValue(OwnerFieldRules.Key(title), out var name) && found.TryAdd(name, column))
                continue;

            var shown = title.Length <= 50 ? title : title[..50];
            if (unknown.Count < 20 && !unknown.Contains(shown, StringComparer.Ordinal))
                unknown.Add(shown);
        }

        var identifies = found.ContainsKey(OwnerImportColumns.OwnerId)
            || found.ContainsKey(OwnerImportColumns.OwnerKey)
            || found.ContainsKey(OwnerImportColumns.FiscalCode)
            || found.ContainsKey(OwnerImportColumns.VatNumber);
        if (!identifies)
            throw new DomainRuleException(BulkImportCodes.ColumnsUnrecognized, "OwnerImportColumnsUnrecognized");

        var data = table.Skip(1).ToList();
        if (data.Count == 0)
            throw new DomainRuleException(BulkImportCodes.FileEmpty, "OnboardingImportFileEmpty");
        if (data.Count > BulkImportLimits.MaxRows)
            throw new DomainRuleException(BulkImportCodes.TooManyRows, "OnboardingImportTooManyRows", BulkImportLimits.MaxRows);

        string? Cell(ImportTableRow row, string name)
        {
            if (!found.TryGetValue(name, out var index) || index >= row.Cells.Count)
                return null;

            var text = row.Cells[index].Trim();
            return text.Length == 0 ? null : text;
        }

        var rows = data
            .Select(row => new ParsedOwnerRow(row.Number)
            {
                PropertyId = Cell(row, OwnerImportColumns.PropertyId),
                Cin = Cell(row, OwnerImportColumns.CinCode),
                OwnerId = Cell(row, OwnerImportColumns.OwnerId),
                OwnerKey = Cell(row, OwnerImportColumns.OwnerKey),
                LegalName = Cell(row, OwnerImportColumns.LegalName),
                Email = Cell(row, OwnerImportColumns.Email),
                EntityKind = Cell(row, OwnerImportColumns.EntityKind),
                TaxResidence = Cell(row, OwnerImportColumns.TaxResidence),
                FiscalCode = Cell(row, OwnerImportColumns.FiscalCode),
                VatNumber = Cell(row, OwnerImportColumns.VatNumber),
                BillingStreet = Cell(row, OwnerImportColumns.BillingStreet),
                BillingCivic = Cell(row, OwnerImportColumns.BillingCivic),
                BillingPostalCode = Cell(row, OwnerImportColumns.BillingPostalCode),
                BillingCity = Cell(row, OwnerImportColumns.BillingCity),
                BillingProvince = Cell(row, OwnerImportColumns.BillingProvince),
                BillingCountry = Cell(row, OwnerImportColumns.BillingCountry),
                SharePercent = Cell(row, OwnerImportColumns.SharePercent),
                OwnershipStart = Cell(row, OwnerImportColumns.OwnershipStart),
                OwnershipEnd = Cell(row, OwnerImportColumns.OwnershipEnd),
                SettlementCurrency = Cell(row, OwnerImportColumns.SettlementCurrency),
                Iban = Cell(row, OwnerImportColumns.Iban),
                Bic = Cell(row, OwnerImportColumns.Bic),
            })
            .ToList();
        return new ParsedOwnerImport(rows, unknown);
    }

    public static string Fingerprint(Guid orgId, IReadOnlyList<ParsedOwnerRow> rows)
    {
        var text = new StringBuilder();
        text.Append("cz-owner-import-v1|").Append(orgId.ToString("N")).Append('\n');
        foreach (var row in rows)
        {
            foreach (var (name, value) in Cells(row))
            {
                if (value is not null)
                    text.Append(name).Append('=').Append(value).Append('\u001f');
            }

            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static IEnumerable<(string Name, string? Value)> Cells(ParsedOwnerRow row)
    {
        yield return (OwnerImportColumns.PropertyId, row.PropertyId);
        yield return (OwnerImportColumns.CinCode, row.Cin);
        yield return (OwnerImportColumns.OwnerId, row.OwnerId);
        yield return (OwnerImportColumns.OwnerKey, row.OwnerKey);
        yield return (OwnerImportColumns.LegalName, row.LegalName);
        yield return (OwnerImportColumns.Email, row.Email);
        yield return (OwnerImportColumns.EntityKind, row.EntityKind);
        yield return (OwnerImportColumns.TaxResidence, row.TaxResidence);
        yield return (OwnerImportColumns.FiscalCode, row.FiscalCode);
        yield return (OwnerImportColumns.VatNumber, row.VatNumber);
        yield return (OwnerImportColumns.BillingStreet, row.BillingStreet);
        yield return (OwnerImportColumns.BillingCivic, row.BillingCivic);
        yield return (OwnerImportColumns.BillingPostalCode, row.BillingPostalCode);
        yield return (OwnerImportColumns.BillingCity, row.BillingCity);
        yield return (OwnerImportColumns.BillingProvince, row.BillingProvince);
        yield return (OwnerImportColumns.BillingCountry, row.BillingCountry);
        yield return (OwnerImportColumns.SharePercent, row.SharePercent);
        yield return (OwnerImportColumns.OwnershipStart, row.OwnershipStart);
        yield return (OwnerImportColumns.OwnershipEnd, row.OwnershipEnd);
        yield return (OwnerImportColumns.SettlementCurrency, row.SettlementCurrency);
        yield return (OwnerImportColumns.Iban, row.Iban);
        yield return (OwnerImportColumns.Bic, row.Bic);
    }

    public static bool TryParseShare(string? text, out decimal share)
    {
        share = 0m;
        var value = text?.Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(value)
            || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0m
            || parsed > OwnerOwnershipTimeline.FullShare
            || decimal.Round(parsed, 2) != parsed)
        {
            return false;
        }

        share = decimal.Round(parsed, 2);
        return true;
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>
    /// A day: <c>2026-03-31</c>, <c>31/03/2026</c>, <c>31-03-2026</c>, <c>31.03.2026</c>, or the serial number of an Excel date cell (days since
    /// 1899-12-30), between the years 1900 and 2200.
    /// </summary>
    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;

        if (value.All(char.IsAsciiDigit) && value.Length is >= 4 and <= 6 && int.TryParse(value, CultureInfo.InvariantCulture, out var serial))
        {
            if (serial is < 1 or > 109_000 || serial < 5_000)
                return false;

            date = new DateOnly(1899, 12, 30).AddDays(serial);
            return date.Year is >= 1900 and <= 2200;
        }

        if (!DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;

        date = DateOnly.FromDateTime(parsed);
        return date.Year is >= 1900 and <= 2200;
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string column, params string[] names)
        {
            foreach (var name in names)
                map[OwnerFieldRules.Key(name)] = column;
        }

        Add(OwnerImportColumns.PropertyId, "propertyId", "property id", "id immobile", "immobile id", "id struttura");
        Add(OwnerImportColumns.CinCode, "cinCode", "cin", "cin code", "codice cin", "cin immobile");
        Add(OwnerImportColumns.OwnerId, "ownerId", "owner id", "casazen owner id", "id proprietario casazen");
        Add(
            OwnerImportColumns.OwnerKey,
            "ownerKey",
            "owner key",
            "owner external key",
            "external key",
            "externalKey",
            "chiave proprietario",
            "codice proprietario",
            "id proprietario",
            "owner code",
            "codice anagrafica");
        Add(
            OwnerImportColumns.LegalName,
            "legalName",
            "legal name",
            "ragione sociale",
            "denominazione",
            "nome legale",
            "nome e cognome",
            "nominativo",
            "owner name",
            "nome proprietario",
            "proprietario",
            "intestatario");
        Add(OwnerImportColumns.Email, "email", "e-mail", "mail", "posta elettronica", "indirizzo email");
        Add(
            OwnerImportColumns.EntityKind,
            "entityKind",
            "entity kind",
            "person kind",
            "tipo soggetto",
            "tipo proprietario",
            "natura giuridica",
            "tipo persona",
            "kind",
            "tipo");
        Add(
            OwnerImportColumns.TaxResidence,
            "taxResidence",
            "tax residence",
            "residenza fiscale",
            "country of tax residence",
            "paese residenza fiscale",
            "nazione residenza fiscale");
        Add(OwnerImportColumns.FiscalCode, "fiscalCode", "fiscal code", "codice fiscale", "cf", "c.f.", "tax id", "taxId", "tax code");
        Add(OwnerImportColumns.VatNumber, "vatNumber", "vat number", "vat id", "vat", "partita iva", "p.iva", "piva", "p iva");
        Add(
            OwnerImportColumns.BillingStreet,
            "billingStreet",
            "billing street",
            "via fatturazione",
            "indirizzo fatturazione",
            "indirizzo di fatturazione",
            "indirizzo_via",
            "via",
            "indirizzo",
            "street");
        Add(
            OwnerImportColumns.BillingCivic,
            "billingCivic",
            "billing civic",
            "civico",
            "numero civico",
            "civico fatturazione",
            "indirizzo_civico",
            "building number");
        Add(OwnerImportColumns.BillingPostalCode, "billingPostalCode", "billing postal code", "cap", "cap fatturazione", "postal code", "zip");
        Add(
            OwnerImportColumns.BillingCity,
            "billingCity",
            "billing city",
            "comune",
            "citta",
            "città",
            "citta fatturazione",
            "city",
            "localita",
            "località",
            "town");
        Add(OwnerImportColumns.BillingProvince, "billingProvince", "billing province", "provincia", "prov", "province");
        Add(OwnerImportColumns.BillingCountry, "billingCountry", "billing country", "paese", "nazione", "country", "paese fatturazione");
        Add(
            OwnerImportColumns.SharePercent,
            "sharePercent",
            "share percent",
            "quota",
            "quota %",
            "quota percentuale",
            "percentuale",
            "percentuale proprieta",
            "share",
            "share %");
        Add(
            OwnerImportColumns.OwnershipStart,
            "ownershipStart",
            "ownership start",
            "inizio proprieta",
            "data inizio",
            "start date",
            "start",
            "valid from",
            "dal",
            "inizio mandato",
            "mandate start",
            "data inizio mandato");
        Add(
            OwnerImportColumns.OwnershipEnd,
            "ownershipEnd",
            "ownership end",
            "fine proprieta",
            "data fine",
            "end date",
            "end",
            "valid to",
            "al",
            "fine mandato",
            "mandate end",
            "data fine mandato");
        Add(
            OwnerImportColumns.SettlementCurrency,
            "settlementCurrency",
            "settlement currency",
            "valuta",
            "valuta di regolamento",
            "currency",
            "valuta pagamento");
        Add(OwnerImportColumns.Iban, "iban", "iban proprietario");
        Add(OwnerImportColumns.Bic, "bic", "swift", "bic/swift", "bic swift", "codice bic");
        return map;
    }
}
