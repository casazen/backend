using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// What is still missing in one section of the billing setup (E11/F01/US01/T04). The epics that own a piece of the setup add a contributor for it
/// (the owner ledger adds <see cref="BillingSetupSections.Channels"/> and <see cref="BillingSetupSections.Mandates"/>, the Italian owner issuers add to
/// <see cref="BillingSetupSections.Italy"/>) and register it next to the others, without touching <see cref="IBillingSetupService"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what is missing.</b> The answer holds the missing items of the section, never the ones that are done: an empty list means «nothing is
/// missing». A section may have several contributors: their items are joined in the order the contributors are registered.</item>
/// <item><b>Reads only, for this organization.</b> A contributor never writes, names the org in every query (it may run in a job with no tenant of its own)
/// and returns codes and counts only, no personal data. It does not throw for something that is not done (that is an answer).</item>
/// <item><b>The order is the order of asking.</b> The first item of the first section that has one is the reason of the part «Fatturazione» of the guide,
/// so a contributor lists first what the user has to do first.</item>
/// </list>
/// </remarks>
public interface IBillingSetupContributor
{
    /// <summary>The section the items belong to: one of <see cref="BillingSetupSections"/>.</summary>
    string Section { get; }

    /// <summary>The items still missing in the section for <paramref name="orgId"/>.</summary>
    Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct);
}

/// <summary>
/// The whole setup of the invoicing of an organization in one place (NF-82, NF-104, NF-118, E11/F01/US01/T04): the sections of the wizard «Fatturazione» with
/// their values and what is missing, the save of one section, and the progress the guide reads. CasaZen issues every document in the name of the organization
/// (decision of 2026-11-14), so everything the invoicing needs is set here, and the part «Fatturazione» of the onboarding guide is done only when nothing is
/// missing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Behind the flag.</b> The endpoints are 404 while <c>Features:FiscalDocumentIssuance</c> is off; the guide reads the progress through
/// <see cref="GetProgressAsync"/> either way (nothing is done while the flag is off).</item>
/// <item><b>Draft and idempotent.</b> A save may carry only some of the fields of the section and never clears the ones it does not carry; saving the same
/// answers again changes nothing. A refused save changes nothing.</item>
/// <item><b>Tenant.</b> The service works through the tenant query filter of its caller and names the org of every row it reads or writes. The web layer
/// decides who may change the setup (the whole organization, not some of its properties, as for <see cref="IDocumentIssuerService"/>).</item>
/// <item><b>One source of truth.</b> The values of the issuer are those of <see cref="IDocumentIssuerService"/>, and its validation is the one of the issuer
/// setting: the Italian VAT number and the fiscal code stay on the organization (its tax profile) and are only read here.</item>
/// </list>
/// </remarks>
public interface IBillingSetupService
{
    /// <summary>The setup as sections, in the order of the wizard, each with its values and its missing items.</summary>
    Task<BillingSetup> GetAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the fields of one section and answers the section as saved. 404 <c>billing_setup_section_unknown</c> for a name that is not a section; 422
    /// <c>billing_setup_section_not_writable</c> for a section another endpoint saves (<c>channels</c>, <c>mandates</c>), <c>billing_setup_field_not_in_section</c>
    /// for a field of another section, <c>issuer_field_invalid</c> (with the <c>field</c> and the list of <c>errors</c>) for a value that is not valid,
    /// <c>issuer_regime_country_mismatch</c> for a regime of the other country, <c>document_issuance_not_available</c> while the flag is off. A payload with no
    /// field saves nothing. Nothing is saved when it is refused.
    /// </summary>
    Task<BillingSetupSection> PutSectionAsync(
        Guid orgId,
        string section,
        BillingSetupPayload payload,
        string? updatedByUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// How many of the sections that apply have nothing missing, and the missing items, for the <paramref name="sections"/> asked (<c>null</c> = all of them).
    /// Only the contributors of those sections run. The part «Fatturazione» reads all of them; the flow «Documents» reads the issuer and the VAT.
    /// </summary>
    Task<BillingSetupProgress> GetProgressAsync(Guid orgId, IReadOnlyCollection<string>? sections = null, CancellationToken ct = default);
}

/// <summary>The stable codes of the errors of the billing setup, with the keys of their messages in <c>SharedResources</c> (the codes of the issuer are <see cref="DocumentIssuerErrorCodes"/>).</summary>
public static class BillingSetupErrorCodes
{
    /// <summary>404: the name is not a section of the billing setup.</summary>
    public const string SectionUnknown = "billing_setup_section_unknown";

    /// <summary>422: the section exists but is saved by the endpoint of the epic that owns it, not by this one.</summary>
    public const string SectionNotWritable = "billing_setup_section_not_writable";

    /// <summary>422: the payload carries a field that belongs to another section; the <c>field</c> is in the answer.</summary>
    public const string FieldNotInSection = "billing_setup_field_not_in_section";

    /// <summary>The key in <c>SharedResources</c> of the message of <paramref name="code"/>.</summary>
    public static string MessageKeyOf(string code) => code switch
    {
        SectionUnknown => "BillingSetupSectionUnknown",
        SectionNotWritable => "BillingSetupSectionNotWritable",
        FieldNotInSection => "BillingSetupFieldNotInSection",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a code of the billing setup."),
    };
}
