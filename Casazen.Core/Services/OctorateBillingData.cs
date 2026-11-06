using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The billing data of the booker of a reservation, as the customer record (the address book) of Octorate holds it and as CasaZen writes it back
/// (FIS-01, FIS-02 point 1, E06/F02/US01/T04): only what an invoice needs, who it is made out to, the fiscal identity, the address, the code of the
/// recipient on the SDI and the PEC. <b>Never</b> an identity document, anything of the Alloggiati data, a phone number or the e-mail address of the
/// guest: the type has no member for them, so no caller can send one (AC-E06-F02-US01-04, <c>OctorateBillingSync_OnlyBillingFieldsSent_Test</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every member is optional; <c>null</c> is «not there». Text is trimmed and never empty.</item>
/// <item><see cref="Address"/> is one line, the way an address book keeps it: the street and the house number of CasaZen's invoice data, joined.</item>
/// <item><see cref="IsCompany"/> says whether <see cref="Name"/> is the denomination of a company (the record's company name, or a VAT number) or the
/// name of a person. A person's name is only read, never written: the record already holds the guest's name, and the name on an invoice must not
/// rewrite who the guest is.</item>
/// <item><b>Personal data.</b> <see cref="ToString"/> names the type and nothing else, so a log line that prints the value leaks nothing.</item>
/// </list>
/// </remarks>
public sealed record OctorateBillingData
{
    /// <summary>True when <see cref="Name"/> is the denomination of a company; false for a person (or when Octorate does not say).</summary>
    public bool IsCompany { get; init; }

    /// <summary>The denomination of the company, or the name of the person.</summary>
    public string? Name { get; init; }

    /// <summary>Codice fiscale of the recipient.</summary>
    public string? FiscalCode { get; init; }

    /// <summary>VAT number of the recipient, with the country prefix when Octorate has it.</summary>
    public string? VatNumber { get; init; }

    /// <summary>The address on one line: street and house number.</summary>
    public string? Address { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    /// <summary>The province: two letters for an Italian one.</summary>
    public string? Province { get; init; }

    /// <summary>The country of the recipient, ISO 3166-1 alpha-2.</summary>
    public string? CountryCode { get; init; }

    /// <summary>The code of the recipient on the SDI (7 letters or digits).</summary>
    public string? SdiRecipientCode { get; init; }

    /// <summary>The certified e-mail (PEC) of the recipient, the address an invoice is sent to: not the e-mail address of the guest.</summary>
    public string? Pec { get; init; }

    /// <summary>True when at least one member holds a text (a company flag alone is nothing to read or to write).</summary>
    public bool HasContent =>
        new[] { Name, FiscalCode, VatNumber, Address, PostalCode, City, Province, CountryCode, SdiRecipientCode, Pec }
            .Any(text => !string.IsNullOrWhiteSpace(text));

    /// <summary>
    /// The billing data of the invoice data CasaZen holds, ready to be written to Octorate: the street and the house number as one line, the kind of
    /// recipient as <see cref="IsCompany"/>. <c>null</c> when the invoice data holds nothing about the recipient (only the guest's «yes»): there is
    /// nothing to send, and an empty record is never sent to overwrite Octorate's.
    /// </summary>
    public static OctorateBillingData? From(InvoiceDataView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!view.HasRecipientData)
            return null;

        var address = string.Join(' ', new[] { view.AddressStreet, view.AddressCivic }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return new OctorateBillingData
        {
            IsCompany = view.RecipientKind == InvoiceRecipientKind.Company,
            Name = Clean(view.Name),
            FiscalCode = Clean(view.FiscalCode),
            VatNumber = Clean(view.VatNumber),
            Address = Clean(address),
            PostalCode = Clean(view.PostalCode),
            City = Clean(view.City),
            Province = Clean(view.Province),
            CountryCode = Clean(view.CountryCode),
            SdiRecipientCode = Clean(view.SdiRecipientCode),
            Pec = Clean(view.Pec),
        };
    }

    /// <summary>
    /// What this data sends to the invoice data of a booking: the same members, the address as the street (Octorate keeps no separate house number).
    /// A member that is <c>null</c> is not sent, so it keeps what is stored.
    /// </summary>
    public SaveInvoiceDataCommand ToInvoiceData() => new()
    {
        RecipientKind = IsCompany ? InvoiceRecipientKind.Company : InvoiceRecipientKind.Guest,
        Name = Name,
        FiscalCode = FiscalCode,
        VatNumber = VatNumber,
        AddressStreet = Address,
        PostalCode = PostalCode,
        City = City,
        Province = Province,
        CountryCode = CountryCode,
        SdiRecipientCode = SdiRecipientCode,
        Pec = Pec,
    };

    /// <summary>The name of the type: the value is personal data and never appears in a log or an exception.</summary>
    public override string ToString() => nameof(OctorateBillingData);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
