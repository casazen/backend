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

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public OwnerEntityKind EntityKind { get; set; } = OwnerEntityKind.NaturalPerson;

    public string? LegalName { get; set; }

    [MaxLength(CountryMaxLength)]
    public string? TaxResidenceCountry { get; set; }

    public string? LegalAddress { get; set; }

    public string? FiscalCode { get; set; }

    public string? VatNumber { get; set; }

    public string? ForeignTaxId { get; set; }

    [MaxLength(CurrencyMaxLength)]
    public string? SettlementCurrency { get; set; }

    public string? BankAccountReference { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? MandateRole { get; set; }

    public DateOnly? MandateFrom { get; set; }

    public DateOnly? MandateTo { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? RevenueRecognitionRule { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? ExpenseCommissionModel { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? WorkingCapitalRule { get; set; }

    [MaxLength(JurisdictionMaxLength)]
    public string? TaxJurisdiction { get; set; }

    public Guid? AccountingPolicyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
