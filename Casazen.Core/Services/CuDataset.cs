using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>The stable codes of the exports of the tax reporting of the owners (E18/F01/US02, NF-117): ProblemDetails <c>code</c>.</summary>
public static class TaxReportingErrors
{
    /// <summary>
    /// 422: the annual data stored for the year was generated before it carried the recipients and the stays of each recipient (an older shape): generate it again while
    /// it is only generated. Nothing is exported from it, never a file with the recipients left empty.
    /// </summary>
    public const string CuDatasetOutdated = "tax_cu_dataset_outdated";

    /// <summary>404: the annual data of the year has no record for that owner (nobody was withheld on its name, or its withholding is on the signatory of its property).</summary>
    public const string CuRecordNotFound = "tax_cu_record_not_found";
}

/// <summary>How a record of the annual data came to be (<see cref="CuRecord.Attribution"/>): the codes are part of the stored payload, never rename one.</summary>
public static class CuAttributions
{
    /// <summary>The withholding evidence of the owner itself: the owner of a property on its own, or a co-owner with nothing on the signatory.</summary>
    public const string Owner = "owner";

    /// <summary>
    /// The co-owner who signed the mandate of a property of several co-owners, and the evidence of the other co-owners on it (⚖ I6, the default: «altrimenti sul firmatario»):
    /// <see cref="CuRecord.Shares"/> says what each co-owner puts into it.
    /// </summary>
    public const string Signatory = "signatory";

    /// <summary>A co-owner whose own share is written on its own name, because the mandate of the signatory says so (<c>CoOwnerTaxReporting.PerShare</c>).</summary>
    public const string Share = "share";
}

/// <summary>
/// Who the Certificazione Unica is made out to, as the registry and the fiscal profile in force at the payment say it (E18/F01/US02/T01): the name, the identifiers and the address.
/// Personal data: it travels only inside the encrypted payload of the annual data and in the files the elevated finance permission downloads, never in a log. A field the agency
/// has not got is <c>null</c>, never invented, and the code of what is missing is in <see cref="Missing"/>.
/// </summary>
public sealed record CuRecipient
{
    /// <summary>The legal name, else first and last name: the one the owner accounting names the owner by.</summary>
    public string? Name { get; init; }

    /// <summary>The first name of a natural person (the CU asks surname and name apart); <c>null</c> for a legal entity.</summary>
    public string? FirstName { get; init; }

    /// <summary>The surname of a natural person; <c>null</c> for a legal entity.</summary>
    public string? LastName { get; init; }

    /// <summary><c>NaturalPerson</c> or <c>LegalEntity</c>, as declared; <c>null</c> until declared.</summary>
    public string? EntityKind { get; init; }

    /// <summary>The Italian fiscal code of the profile in force, else the one of the registry.</summary>
    public string? FiscalCode { get; init; }

    /// <summary>The VAT number of the profile in force, else the one of the registry.</summary>
    public string? VatNumber { get; init; }

    /// <summary>The country of tax residence (ISO 3166-1 alpha-2), when declared.</summary>
    public string? TaxResidenceCountry { get; init; }

    /// <summary>The structured address of the registry: the street.</summary>
    public string? Street { get; init; }

    /// <summary>The building number.</summary>
    public string? Civic { get; init; }

    /// <summary>The postal code.</summary>
    public string? PostalCode { get; init; }

    /// <summary>The town.</summary>
    public string? City { get; init; }

    /// <summary>The province.</summary>
    public string? Province { get; init; }

    /// <summary>The country of the address (ISO 3166-1 alpha-2).</summary>
    public string? Country { get; init; }

    /// <summary>The legal address of the fiscal profile, one text: the address when the registry has no structured one.</summary>
    public string? LegalAddress { get; init; }

    /// <summary>What the Certificazione Unica of this recipient cannot say: <c>name</c>, <c>fiscal_code</c>, <c>address</c> (none when the data is complete).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>The property a stay of a record is in, as the registry has it when the data is generated (the short-let section of the CU names it).</summary>
public sealed record CuProperty
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The street address of the property.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>The apartment inside the building (interno, scala), when it has one.</summary>
    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    /// <summary>The ISTAT code of the comune, when the host chose it.</summary>
    public string? ComuneIstatCode { get; init; }

    /// <summary>The national identification code (CIN), when stored.</summary>
    public string? Cin { get; init; }

    /// <summary>The cadastral sheet (foglio), when stored.</summary>
    public string? CadastralSheet { get; init; }

    /// <summary>The cadastral parcel (particella), when stored.</summary>
    public string? CadastralParcel { get; init; }

    /// <summary>The cadastral subaltern, when stored.</summary>
    public string? CadastralSubaltern { get; init; }

    /// <summary>The cadastral category, when stored.</summary>
    public string? CadastralCategory { get; init; }
}

/// <summary>What one co-owner puts into the record of the signatory (<see cref="CuAttributions.Signatory"/>): its own base and its own withholding.</summary>
public sealed record CuShare
{
    public Guid OwnerId { get; init; }

    public string? Name { get; init; }

    public decimal GrossBase { get; init; }

    public decimal Withheld { get; init; }
}

/// <summary>
/// One piece of a record: the base and the withholding of one stay, in euro, as the evidence of a payout of the year decided them (a reversal has the opposite amounts). The
/// withholding of a payout is decided once on the whole base of its stays; each stay carries its part of it, split in proportion to its base to the cent, so the pieces always add up
/// to the evidence. A piece with no stay (<see cref="BookingId"/> <c>null</c>) is evidence whose facts do not name its stays (a payout of before the facts were read from the
/// ledger): its amounts are the evidence's, with no detail.
/// </summary>
public sealed record CuLine
{
    public Guid? BookingId { get; init; }

    public Guid? PropertyId { get; init; }

    public DateOnly? CheckIn { get; init; }

    public DateOnly? CheckOut { get; init; }

    public int? Nights { get; init; }

    /// <summary>The owner whose payout it came from (the co-owner, in the record of the signatory).</summary>
    public Guid SourceOwnerId { get; init; }

    /// <summary>The month (1-12) of the payment, the period of the F24 the withholding went into.</summary>
    public int Month { get; init; }

    public decimal GrossBase { get; init; }

    public decimal Withheld { get; init; }
}

/// <summary>
/// The data of the Certificazione Unica of one recipient for the year (E18/F01/US02/T01): who it is made out to, the properties and the stays it covers, the base and the
/// withholding in euro. The first four properties are the ones the annual data has always carried (<c>ownerId</c>, <c>grossBase</c>, <c>withheld</c>, <c>evidence</c>), so what reads
/// them keeps working; the rest is what the export for the accountant needs (⚖ T2: the telematic record layout is not produced).
/// </summary>
public sealed record CuRecord
{
    /// <summary>The owner the record is on: the recipient.</summary>
    public Guid OwnerId { get; init; }

    public decimal GrossBase { get; init; }

    public decimal Withheld { get; init; }

    /// <summary>How many decisions of payouts, and reversals of them, make the record.</summary>
    public int Evidence { get; init; }

    /// <summary>Who it is made out to; <c>null</c> only in the data generated before the recipients were stored (<see cref="CuDatasetPayload.IsDetailed"/>).</summary>
    public CuRecipient? Recipient { get; init; }

    /// <summary>One of <see cref="CuAttributions"/>.</summary>
    public string Attribution { get; init; } = CuAttributions.Owner;

    /// <summary>The tax code of the F24 the withholding was paid with (<c>1919</c> while the approved rule says so).</summary>
    public string? TaxCode { get; init; }

    /// <summary>The rule set the decisions were taken on (<c>IT-STR-WITHHOLDING@2026.1</c>), each once.</summary>
    public IReadOnlyList<string> RuleVersions { get; init; } = [];

    /// <summary>The co-owners the record adds up, with what each puts into it: only for the record of a signatory, empty otherwise.</summary>
    public IReadOnlyList<CuShare> Shares { get; init; } = [];

    /// <summary>The properties of the stays, each once.</summary>
    public IReadOnlyList<CuProperty> Properties { get; init; } = [];

    /// <summary>The stays, by owner of the payout, property and check-in.</summary>
    public IReadOnlyList<CuLine> Lines { get; init; } = [];
}

/// <summary>The annual Certificazione Unica data as it was built from the evidence of the year, before it is stored.</summary>
/// <param name="Year">The year of the payments.</param>
/// <param name="Records">The records, by recipient name.</param>
/// <param name="TotalGrossBase">The base of every decision and reversal of the year, in euro: the sum of the records.</param>
/// <param name="TotalWithheld">The withholding of every decision and reversal of the year, in euro: the sum of the records.</param>
/// <param name="EvidenceCount">How many decisions and reversals the year has.</param>
/// <param name="EvidenceSha256">The SHA-256 of the ids and the amounts of that evidence.</param>
public sealed record CuDataset(
    int Year,
    IReadOnlyList<CuRecord> Records,
    decimal TotalGrossBase,
    decimal TotalWithheld,
    int EvidenceCount,
    string EvidenceSha256);

/// <summary>The annual data of a year as stored (with its filing state) and its records read from the encrypted payload.</summary>
public sealed record StoredCuDataset(TaxReportingDataset Dataset, IReadOnlyList<CuRecord> Records);

/// <summary>The payload of the annual data: its JSON, written and read in one place (the column is encrypted at rest, <c>EncryptedColumns.TaxReportingDatasetPurpose</c>).</summary>
public static class CuDatasetPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON of <paramref name="records"/>: an array, the first four properties of each the ones the annual data always had.</summary>
    public static string Serialize(IReadOnlyList<CuRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return JsonSerializer.Serialize(records, Json);
    }

    /// <summary>
    /// The records of a stored payload; a payload that cannot be read is a damaged dataset and fails (<see cref="JsonException"/>), never an empty list. The data stored before the
    /// recipients and the stays existed has only the four properties: its records have none of the rest (<see cref="IsDetailed"/> says it).
    /// </summary>
    public static IReadOnlyList<CuRecord> Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Deserialize<List<CuRecord>>(payload, Json) ?? [];
    }

    /// <summary>True when every record names its recipient: the data was generated with the recipients and the stays (an older shape has none).</summary>
    public static bool IsDetailed(IReadOnlyList<CuRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records.All(r => r.Recipient is not null);
    }

    /// <summary>
    /// The payload as the API of the annual data answers it: the totals of each owner and nothing else (<c>ownerId</c>, <c>grossBase</c>, <c>withheld</c>, <c>evidence</c>), the shape it
    /// always had. The recipients are personal data (fiscal code, address): they are read only by the exports of the elevated finance permission. <c>[]</c> for a payload that cannot be read.
    /// </summary>
    public static string TotalsJson(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return JsonSerializer.Serialize(Parse(payload).Select(r => new { r.OwnerId, r.GrossBase, r.Withheld, r.Evidence }), Json);
        }
        catch (JsonException)
        {
            return "[]";
        }
    }
}
