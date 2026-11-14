using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The data of the issuer a fiscal document was issued with (E11/F01/US01/T01): written as JSON into <see cref="FiscalDocument.IssuerSnapshot"/> at the
/// issue (encrypted at rest), so the PDF and the XML of an issued document show the issuer as it was, whatever the profile says later. A draft, and a
/// document issued before the snapshot existed, are shown with the current profile (<see cref="From"/>).
/// </summary>
/// <remarks>
/// The bank account is kept masked (<see cref="IssuerBankAccounts.Mask"/>): the document needs to say which account, not to carry it. The one exception is
/// the payment part of a Swiss owner invoice, which prints the account it is paid on (<see cref="QrBillAccount"/>, E14/F01/US01/T02). The Italian VAT
/// number and fiscal code are the organization's (<c>Org</c>), the one source of the Italian fiscal identity.
/// </remarks>
public sealed record FiscalIssuerSnapshot
{
    /// <summary>The version of the JSON shape.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The legal name of the issuer.</summary>
    public string? LegalName { get; init; }

    /// <summary>The name of the organization, shown when the profile has no legal name (a draft of an issuer that has not finished the wizard).</summary>
    public string? OrgName { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    /// <summary>The country of the issuer: <c>IT</c> or <c>CH</c> (the Swiss layout of the PDF is chosen by it).</summary>
    public string CountryCode { get; init; } = OrgFiscalIssuerProfile.DefaultCountryCode;

    /// <summary>The Italian VAT number of the organization; <c>null</c> when it has none.</summary>
    public string? VatNumber { get; init; }

    /// <summary>The Italian fiscal code of the organization.</summary>
    public string? FiscalCode { get; init; }

    /// <summary>The Swiss VAT number (UID) of a registered Swiss issuer, <c>CHE-123.456.789</c>.</summary>
    public string? SwissVatNumber { get; init; }

    /// <summary>The issuer is registered for the VAT.</summary>
    public bool VatRegistered { get; init; }

    /// <summary>The regime of the issuer.</summary>
    public DocumentRegime? Regime { get; init; }

    /// <summary>The regime code of FatturaPA that was used (the profile's own, else the rule's). Italian issuers only.</summary>
    public string? FatturaPaRegimeCode { get; init; }

    /// <summary>The IBAN of the issuer, masked.</summary>
    public string? IbanMasked { get; init; }

    /// <summary>
    /// The account the QR-bill of a Swiss owner invoice is paid on (E14/F01/US01/T02): the QR-IBAN of the issuer, else its IBAN of Switzerland or Liechtenstein,
    /// <b>in clear</b>, because the payment part prints it. Kept only in the snapshot of a Swiss owner invoice (<c>null</c> for every other document and for
    /// an issuer without such an account: the invoice then says the payment is as the manager says); the snapshot is encrypted at rest.
    /// </summary>
    public string? QrBillAccount { get; init; }

    /// <summary>True for an issuer in Switzerland.</summary>
    [JsonIgnore]
    public bool IsSwiss => string.Equals(CountryCode, "CH", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name printed for the issuer: its legal name, else the name of the organization.</summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(LegalName) ? LegalName : OrgName ?? string.Empty;

    /// <summary>
    /// The snapshot of the issuer as the profile and the organization say now. <paramref name="ruleRegimeCode"/> is the regime code of FatturaPA of the
    /// tax rule, used when the profile has none of its own; <paramref name="withQrBillAccount"/> keeps the account of the QR-bill (a Swiss owner invoice only).
    /// </summary>
    public static FiscalIssuerSnapshot From(OrgFiscalIssuerProfile? profile, Org org, string? ruleRegimeCode = null, bool withQrBillAccount = false)
    {
        ArgumentNullException.ThrowIfNull(org);

        var country = string.IsNullOrWhiteSpace(profile?.CountryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : profile.CountryCode.Trim().ToUpperInvariant();
        var swiss = string.Equals(country, "CH", StringComparison.Ordinal);
        return new FiscalIssuerSnapshot
        {
            LegalName = Text(profile?.LegalName),
            OrgName = Text(org.Name),
            AddressStreet = Text(profile?.AddressStreet),
            AddressCivic = Text(profile?.AddressCivic),
            PostalCode = Text(profile?.PostalCode),
            City = Text(profile?.City),
            Province = Text(profile?.Province),
            CountryCode = country,
            VatNumber = org.HasPartitaIva ? Text(org.PartitaIvaNumber) : null,
            FiscalCode = Text(org.FiscalCode),
            SwissVatNumber = swiss ? Text(profile?.SwissVatNumber) : null,
            VatRegistered = profile?.VatRegistered ?? false,
            Regime = profile?.DocumentRegime,
            FatturaPaRegimeCode = swiss ? null : Text(profile?.FatturaPaRegimeCode) ?? Text(ruleRegimeCode),
            IbanMasked = IssuerBankAccounts.Mask(profile?.Iban),
            QrBillAccount = withQrBillAccount && swiss ? SwissQrBill.AccountOf(profile?.QrIban, profile?.Iban) : null,
        };
    }

    /// <summary>The snapshot as it is stored.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The snapshot stored in <paramref name="json"/>, or <c>null</c> when there is none or it cannot be read (the caller falls back to the profile).</summary>
    public static FiscalIssuerSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<FiscalIssuerSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
