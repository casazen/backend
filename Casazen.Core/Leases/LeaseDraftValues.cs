using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Leases;

/// <summary>
/// What the landlord typed in the five data steps of the wizard (LR-12), exactly as typed: nothing is corrected, rounded or
/// refused, because the draft keeps incomplete and wrong values and says what is missing (<see cref="LeaseDraftProgress"/>).
/// This is the document stored, encrypted, in <c>LeaseContract.DraftData</c> and the <c>values</c> of <c>GET api/leases/{id}/draft</c>.
/// </summary>
/// <remarks>
/// The type, the regime and the start date are strings because they are kept even when they name nothing valid ("Studenti" is a
/// type CasaZen does not have, a start date can be half typed); the interpreted values are
/// <see cref="LeaseDraftRules"/>. Numbers are the numbers the client sent. Contains personal data: it is only ever written to the
/// encrypted column and sent to a caller who may create the lease.
/// </remarks>
public sealed record LeaseDraftValues
{
    public Guid? PropertyId { get; init; }

    public IReadOnlyList<LeaseDraftParty> Landlords { get; init; } = [];

    public IReadOnlyList<LeaseDraftParty> Tenants { get; init; } = [];

    public string? ContractType { get; init; }

    public string? TaxRegime { get; init; }

    public decimal? MonthlyRent { get; init; }

    public decimal? SecurityDeposit { get; init; }

    public LeaseDraftConcordato? Concordato { get; init; }

    /// <summary>
    /// Step 3, <b>transitory type only</b> (TR, D.M. 16/01/2017 art. 2 c. 4): who has the need that justifies the transitory term,
    /// <c>Landlord</c> or <c>Tenant</c>. Kept as typed; the other types never read it, and nor does a contract signed elsewhere.
    /// </summary>
    public string? TransitoryParty { get; init; }

    public string? TransitoryNeed { get; init; }

    public string? StartDate { get; init; }

    /// <summary>
    /// Step 4, <b>transitory type and imported contracts</b> (LR-13, TR): the last day of the first period, <c>yyyy-MM-dd</c>. An imported
    /// contract states it as the contract already signed does; a transitory contract made in CasaZen has a term of its own, free from 1 to
    /// 18 months, so the landlord types it. Without it the term follows the type (48 months for the libero, 36 for the concordato) and the
    /// transitory type asks for it. The libero and the concordato made in CasaZen never read it: the wizard owns their term.
    /// </summary>
    public string? EndDate { get; init; }

    /// <summary>
    /// Step 4, transitory type only: the documentation attached to prove the need (at most 300 characters). Asked for when the term lasts
    /// more than 30 days (<see cref="LeaseContractTerms.TransitorioDocumentationAfterDays"/>), because it is the term typed in this step that
    /// decides it.
    /// </summary>
    public string? TransitoryDocumentation { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LeaseDraftValues? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LeaseDraftValues>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record LeaseDraftParty
{
    public Guid Id { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? FiscalCode { get; init; }

    public string? Citizenship { get; init; }

    public string? Residence { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public string? DocumentsVerifiedOn { get; init; }
}

public sealed record LeaseDraftConcordato
{
    public decimal Sqm { get; init; }

    public int TypeAElementCount { get; init; }

    public int TypeBElementCount { get; init; }

    public int TypeCElementCount { get; init; }

    public int TypeDElementCount { get; init; }

    public int QualifyingTypeDElementCount { get; init; }

    public bool IsFurnished { get; init; }

    public bool StoveHeating { get; init; }

    public bool AirConditioning { get; init; }

    public decimal GarageSqm { get; init; }

    public decimal BalconySqm { get; init; }

    public decimal OtherAppurtenanceSqm { get; init; }

    public decimal PrivateGreenSqm { get; init; }

    public string? ZoneName { get; init; }

    public string? CadastralSheet { get; init; }
}
