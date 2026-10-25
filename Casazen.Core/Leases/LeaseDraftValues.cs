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
    /// <summary>Step 1: the property of the contract.</summary>
    public Guid? PropertyId { get; init; }

    /// <summary>Step 2: the landlords, in the order entered.</summary>
    public IReadOnlyList<LeaseDraftParty> Landlords { get; init; } = [];

    /// <summary>Step 2: the tenants, in the order entered.</summary>
    public IReadOnlyList<LeaseDraftParty> Tenants { get; init; } = [];

    /// <summary>Step 3: <c>Libero</c>, <c>Concordato</c> or <c>Transitorio</c> (anything else is kept and reported as not available).</summary>
    public string? ContractType { get; init; }

    /// <summary>Step 3: <c>CedolareSecca</c> or <c>Ordinario</c>.</summary>
    public string? TaxRegime { get; init; }

    /// <summary>Step 3: monthly rent in euros.</summary>
    public decimal? MonthlyRent { get; init; }

    /// <summary>Step 3: security deposit in euros; <c>null</c> when there is none.</summary>
    public decimal? SecurityDeposit { get; init; }

    /// <summary>Step 3: the characteristics of the unit that fix the range of a canone concordato.</summary>
    public LeaseDraftConcordato? Concordato { get; init; }

    /// <summary>
    /// Step 3, <b>transitory type only</b> (TR, D.M. 16/01/2017 art. 2 c. 4): who has the need that justifies the transitory term,
    /// <c>Landlord</c> or <c>Tenant</c>. Kept as typed; the other types never read it, and nor does a contract signed elsewhere.
    /// </summary>
    public string? TransitoryParty { get; init; }

    /// <summary>Step 3, transitory type only: the need itself, in a few words (at most 500 characters).</summary>
    public string? TransitoryNeed { get; init; }

    /// <summary>Step 4: first day of the contract, <c>yyyy-MM-dd</c>; the end of the first period is computed from the type.</summary>
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

    /// <summary>The JSON document stored in <c>LeaseContract.DraftData</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The values stored in <paramref name="json"/>, or <c>null</c> when there is nothing stored or it cannot be read.</summary>
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

/// <summary>A landlord or a tenant as typed. <see cref="Id"/> is the one the server gave the entry: it is also the id of the party once it is written.</summary>
public sealed record LeaseDraftParty
{
    public Guid Id { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    /// <summary>A 16-character codice fiscale or an 11-digit one (a company), as typed.</summary>
    public string? FiscalCode { get; init; }

    /// <summary>Two letters (ISO 3166-1 alpha-2).</summary>
    public string? Citizenship { get; init; }

    /// <summary>Municipality and address of the residence, or of the registered office of a company.</summary>
    public string? Residence { get; init; }

    /// <summary>Optional: used for the payment links and the reminders.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Step 5: the day the identity document was checked, <c>yyyy-MM-dd</c>; <c>null</c> when it was not.</summary>
    public string? DocumentsVerifiedOn { get; init; }
}

/// <summary>
/// The characteristics of the unit for the range of a canone concordato, as typed (the same fields as
/// <c>RentBandCharacteristics</c>). A field that was not sent is 0, false or empty; the range is computed by the server.
/// </summary>
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
