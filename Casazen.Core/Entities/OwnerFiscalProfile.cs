using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The effective-dated fiscal and accounting profile of a property owner (NF-109, NF-110, ADR-012, E06/F05/US02/T01): who the owner is for the statements,
/// the payouts and the tax evidence from <see cref="ValidFrom"/> to <see cref="ValidTo"/>, and which approved rule set (<see cref="OwnerAccountingPolicy"/>)
/// applies. It is <b>not</b> an invoice-recipient profile: CasaZen issues no agency-to-owner invoice, so there is no SDI code or PEC here.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Privacy (ADR-012).</b> The legal name, the legal address, the fiscal and VAT identifiers, the foreign tax id and the bank account are
/// <b>encrypted at rest</b> (<c>Casazen.OwnerFiscalProfile</c>, no length on the columns); only <c>agency.owner.financial.manage</c> reads them in full,
/// everyone else gets them masked.</item>
/// <item><b>No inference.</b> Neither the country nor the kind of the owner decides a liability: the profile names its rule set and its jurisdiction, and
/// a profile without an approved rule set blocks the financial operations.</item>
/// <item><b>Effective dating.</b> Profiles of one owner never overlap; a document or a payment resolves the one in force on its day.</item>
/// </list>
/// </remarks>
[Table("OwnerFiscalProfiles")]
public class OwnerFiscalProfile : ITenantOwned
{
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="PropertyOwnerId"/>, <see cref="ValidFrom"/>).</summary>
    public const string OwnerValidFromIndexName = "UIX_OwnerFiscalProfiles_OrgId_PropertyOwnerId_ValidFrom";

    public const int LegalNameMaxLength = 200;
    public const int LegalAddressMaxLength = 500;
    public const int IdentifierMaxLength = 40;
    public const int BankAccountMaxLength = 40;
    public const int CountryMaxLength = 2;
    public const int CurrencyMaxLength = 3;
    public const int CodeMaxLength = 32;
    public const int JurisdictionMaxLength = 8;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    /// <summary>The first day the profile applies.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last day the profile applies; <c>null</c> while it is in force.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>A natural person or a legal entity.</summary>
    public OwnerEntityKind EntityKind { get; set; } = OwnerEntityKind.NaturalPerson;

    /// <summary>The legal name. <b>Encrypted at rest.</b></summary>
    public string? LegalName { get; set; }

    /// <summary>The country of tax residence (ISO 3166-1 alpha-2).</summary>
    [MaxLength(CountryMaxLength)]
    public string? TaxResidenceCountry { get; set; }

    /// <summary>The legal address, one text. <b>Encrypted at rest.</b></summary>
    public string? LegalAddress { get; set; }

    /// <summary>The Italian fiscal code. <b>Encrypted at rest.</b></summary>
    public string? FiscalCode { get; set; }

    /// <summary>The VAT number. <b>Encrypted at rest.</b></summary>
    public string? VatNumber { get; set; }

    /// <summary>The tax identifier of another country (the Swiss AVS/UID...). <b>Encrypted at rest.</b></summary>
    public string? ForeignTaxId { get; set; }

    /// <summary>The currency the owner is paid in (ISO 4217).</summary>
    [MaxLength(CurrencyMaxLength)]
    public string? SettlementCurrency { get; set; }

    /// <summary>The bank account the owner is paid on (an IBAN). <b>Encrypted at rest.</b></summary>
    public string? BankAccountReference { get; set; }

    /// <summary>The role of the owner in the mandate (<c>owner</c>, <c>usufructuary</c>, <c>landlord</c>...).</summary>
    [MaxLength(CodeMaxLength)]
    public string? MandateRole { get; set; }

    public DateOnly? MandateFrom { get; set; }

    public DateOnly? MandateTo { get; set; }

    /// <summary>How revenue is recognized for the owner (<c>cash</c>, <c>stay</c>...), from the approved rule set.</summary>
    [MaxLength(CodeMaxLength)]
    public string? RevenueRecognitionRule { get; set; }

    /// <summary>How expenses and the commission are charged (<c>net_of_commission</c>, <c>gross</c>...).</summary>
    [MaxLength(CodeMaxLength)]
    public string? ExpenseCommissionModel { get; set; }

    /// <summary>How working capital is held (<c>none</c>, <c>reserve</c>...).</summary>
    [MaxLength(CodeMaxLength)]
    public string? WorkingCapitalRule { get; set; }

    /// <summary>The tax jurisdiction the profile is under (<c>IT</c>, <c>CH</c>, <c>CH-TI</c>): it must be the one of its rule set.</summary>
    [MaxLength(JurisdictionMaxLength)]
    public string? TaxJurisdiction { get; set; }

    /// <summary>The rule set the profile follows; <c>null</c> while none is chosen (the financial operations are blocked).</summary>
    public Guid? AccountingPolicyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
