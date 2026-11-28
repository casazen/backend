using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public static class TaxReportingErrors
{
    public const string CuDatasetOutdated = "tax_cu_dataset_outdated";

    public const string CuRecordNotFound = "tax_cu_record_not_found";
}

public static class CuAttributions
{
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
    public string? Name { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? EntityKind { get; init; }

    public string? FiscalCode { get; init; }

    /// <summary>The VAT number of the profile in force, else the one of the registry.</summary>
    public string? VatNumber { get; init; }

    public string? TaxResidenceCountry { get; init; }

    public string? Street { get; init; }

    public string? Civic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? Country { get; init; }

    public string? LegalAddress { get; init; }

    public IReadOnlyList<string> Missing { get; init; } = [];
}

public sealed record CuProperty
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    public string? ComuneIstatCode { get; init; }

    public string? Cin { get; init; }

    public string? CadastralSheet { get; init; }

    public string? CadastralParcel { get; init; }

    public string? CadastralSubaltern { get; init; }

    public string? CadastralCategory { get; init; }
}

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
    public Guid OwnerId { get; init; }

    public decimal GrossBase { get; init; }

    public decimal Withheld { get; init; }

    public int Evidence { get; init; }

    public CuRecipient? Recipient { get; init; }

    public string Attribution { get; init; } = CuAttributions.Owner;

    /// <summary>The tax code of the F24 the withholding was paid with (<c>1919</c> while the approved rule says so).</summary>
    public string? TaxCode { get; init; }

    public IReadOnlyList<string> RuleVersions { get; init; } = [];

    /// <summary>The co-owners the record adds up, with what each puts into it: only for the record of a signatory, empty otherwise.</summary>
    public IReadOnlyList<CuShare> Shares { get; init; } = [];

    public IReadOnlyList<CuProperty> Properties { get; init; } = [];

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

public sealed record StoredCuDataset(TaxReportingDataset Dataset, IReadOnlyList<CuRecord> Records);

public static class CuDatasetPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<CuRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return JsonSerializer.Serialize(records, Json);
    }

    public static IReadOnlyList<CuRecord> Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Deserialize<List<CuRecord>>(payload, Json) ?? [];
    }

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
