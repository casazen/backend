namespace Casazen.Core.Leases;

public static class LeaseDraftErrorCodes
{
    public const string Conflict = "lease_draft_conflict";

    public const string NotDraft = "lease_not_draft";

    public const string ReviewBlocked = "lease_review_blocked";

    public const string ReviewNotConfirmed = "lease_review_not_confirmed";

    public const string ReviewNotApplicable = "lease_review_not_applicable";

    public const string SentNotApplicable = "lease_sent_not_applicable";

    public const string NotDeletable = "lease_not_deletable";
}

/// <summary>
/// The closed list of what can be missing in a step of the draft (<c>flow.steps[n].needs[].code</c>) and, with a severity, of the
/// checks of the review (<c>checks[].code</c>). The interface turns each code into a sentence; the parameters are described
/// in <c>docs/runbooks/long-rent-lease-draft.md</c>. A code is never renamed: add a new one.
/// </summary>
public static class LeaseNeedCodes
{
    public const string PropertyMissing = "property_missing";
    public const string ApeMissing = "ape_missing";
    public const string ApeIdentificationMissing = "ape_identification_missing";
    public const string CadastralMissing = "cadastral_missing";

    public const string LandlordMissing = "landlord_missing";
    public const string LandlordIncomplete = "landlord_incomplete";
    public const string TenantMissing = "tenant_missing";
    public const string TenantIncomplete = "tenant_incomplete";
    public const string PartyFiscalCodeDuplicate = "party_fiscal_code_duplicate";

    public const string ContractTypeMissing = "contract_type_missing";
    public const string LeaseTypeNotAvailable = "lease_type_not_available";
    public const string TaxRegimeMissing = "tax_regime_missing";
    public const string CedolareNotAllowedCompany = "cedolare_not_allowed_company";
    public const string RentMissing = "rent_missing";
    public const string RentInvalid = "rent_invalid";
    public const string ConcordatoDataMissing = "concordato_data_missing";
    public const string ConcordatoDataInvalid = "concordato_data_invalid";
    public const string ConcordatoUnavailable = "concordato_unavailable";
    public const string RentOutOfBand = "rent_out_of_band";
    public const string DepositMissing = "deposit_missing";
    public const string DepositInvalid = "deposit_invalid";
    public const string DepositTooHigh = "deposit_too_high";

    public const string TransitoryPartyMissing = "transitory_party_missing";
    public const string TransitoryNeedMissing = "transitory_need_missing";

    public const string StartMissing = "start_missing";
    public const string StartInvalid = "start_invalid";
    public const string EndMissing = "end_missing";
    public const string EndInvalid = "end_invalid";
    public const string TermOutOfRange = "term_out_of_range";

    public const string TransitoryDocumentationMissing = "transitory_documentation_missing";
    public const string TransitoryDocumentationTooLong = "transitory_documentation_too_long";

    public const string PartyDocumentsDateInvalid = "party_documents_date_invalid";

    public const string ReviewNotConfirmed = "review_not_confirmed";
    public const string NotSent = "not_sent";
    public const string SignedCopyMissing = "signed_copy_missing";
    public const string RegistrationMissing = "registration_missing";

    public const string PropertyOk = "property_ok";
    public const string ApeOk = "ape_ok";
    public const string CadastralOk = "cadastral_ok";
    public const string LandlordsOk = "landlords_ok";
    public const string TenantsOk = "tenants_ok";
    public const string RentOk = "rent_ok";
    public const string RentInBand = "rent_in_band";
    public const string DepositOk = "deposit_ok";
    public const string StartOk = "start_ok";
    public const string PropertyShortMode = "property_short_mode";
    public const string StartInPast = "start_in_past";
    public const string LeaseOverlap = "lease_overlap";
    public const string RentOutsideIndicativeBand = "rent_outside_indicative_band";
    public const string TemplateDataMissing = "template_data_missing";
    public const string TenantExtraEu = "tenant_extra_eu";
    public const string TemplateNotApproved = "template_not_approved";
    public const string DocumentsNotVerified = "documents_not_verified";
    public const string TransitoryRentBandToCheck = "transitory_rent_band_to_check";
}
