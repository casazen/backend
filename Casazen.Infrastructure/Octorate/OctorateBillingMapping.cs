using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The names and the shapes of the billing data in Octorate's customer record, the address book of the guests (E06/F02/US01/T04, FIS-01, FIS-02
/// point 1): how the record of the booker is read into <see cref="OctorateBillingData"/> and how <see cref="OctorateBillingData"/> is written back.
/// Pure: no call, no clock, no log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Documented, not proven.</b> The routes (<c>GET /rest/v1/guests/{acc}/{guestId}</c>, <c>PUT /rest/v1/guests/{acc}/{guestId}</c>) are the ones
/// the requirements record from Octorate's specification (S13, S18: the API gives the guests' registry data and billing data, nothing else). The
/// member names below are <b>assumed</b>: this build could not reach the Octorate sandbox (whose guests' sensitive data needs CasaZen's outbound IPs to
/// be whitelisted, D24), so none of them was read from a real answer. The first name of each list is the one written, the others are also read. A
/// member that is not there is not an error: the record simply has no such data. Check them with one real read and write, then fix this class and
/// raise <see cref="ContractVersion"/> (<c>docs/runbooks/feature-flags.md</c>, «Before turning <c>OctorateBillingSync</c> on») before
/// <c>Features:OctorateBillingSync</c> is turned on.</item>
/// <item><b>Only billing members are written</b> (<see cref="WrittenMembers"/>): never a document, an Alloggiati datum, a phone number or the guest's
/// e-mail address, and nothing the record has besides. A person's name is not written (the record already has the guest's name).</item>
/// <item><b>The hash</b> (<see cref="HashOf"/>) is the hash of the body that is written, so it changes when the data does and when this class starts
/// writing something else.</item>
/// </list>
/// </remarks>
internal static class OctorateBillingMapping
{
    /// <summary>Bumped when the shape written below changes, so the data already written is written again in the new shape.</summary>
    public const int ContractVersion = 1;

    /// <summary>The body of data with nothing to write: never sent.</summary>
    public const string EmptyBody = "{}";

    /// <summary>The members of a reservation that name its customer record (assumed).</summary>
    public static readonly string[] ReservationCustomerIdMembers = ["guestId", "customerId"];

    // The members of the customer record, assumed. The first name of each list is written; all of them are read.
    public static readonly string[] CompanyNameMembers = ["companyName", "company", "businessName"];
    public static readonly string[] FiscalCodeMembers = ["taxCode", "fiscalCode", "codiceFiscale"];
    public static readonly string[] VatNumberMembers = ["vatNumber", "vat", "partitaIva"];
    public static readonly string[] AddressMembers = ["address", "billingAddress"];
    public static readonly string[] PostalCodeMembers = ["zipCode", "zip", "postalCode"];
    public static readonly string[] CityMembers = ["city"];
    public static readonly string[] ProvinceMembers = ["province", "state"];
    public static readonly string[] CountryMembers = ["country", "countryCode"];
    public static readonly string[] SdiRecipientCodeMembers = ["sdiCode", "recipientCode", "codiceDestinatario"];
    public static readonly string[] PecMembers = ["pec", "certifiedEmail"];

    // The name of the person is read to complete the recipient, never written.
    private const string FirstNameMember = "firstName";
    private const string LastNameMember = "lastName";

    /// <summary>Every member <see cref="WriteBody"/> can write, and no other: the whole of what leaves CasaZen.</summary>
    public static IReadOnlyList<string> WrittenMembers { get; } =
    [
        CompanyNameMembers[0],
        FiscalCodeMembers[0],
        VatNumberMembers[0],
        AddressMembers[0],
        PostalCodeMembers[0],
        CityMembers[0],
        ProvinceMembers[0],
        CountryMembers[0],
        SdiRecipientCodeMembers[0],
        PecMembers[0],
    ];

    /// <summary>The customer record the reservation names, or <c>null</c> when the reservation names none.</summary>
    public static string? CustomerIdOf(JsonElement reservation) => FirstOf(reservation, ReservationCustomerIdMembers);

    /// <summary>
    /// The billing data of a customer record, or <c>null</c> when the record holds none: a record needs at least a company name, a fiscal code, a VAT
    /// number, a code of the recipient on the SDI, a PEC or an address to count (a guest's name alone is not billing data: the invoice data of
    /// every imported booking would be made of it). A company is a record with a company name or a VAT number.
    /// </summary>
    public static OctorateBillingData? Read(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
            return null;

        var company = FirstOf(record, CompanyNameMembers);
        var vat = FirstOf(record, VatNumberMembers);
        var fiscalCode = FirstOf(record, FiscalCodeMembers);
        var address = FirstOf(record, AddressMembers);
        var sdi = FirstOf(record, SdiRecipientCodeMembers);
        var pec = FirstOf(record, PecMembers);
        if (company is null && vat is null && fiscalCode is null && address is null && sdi is null && pec is null)
            return null;

        var person = string.Join(
            ' ',
            new[] { OctorateHttp.StringOf(record, FirstNameMember), OctorateHttp.StringOf(record, LastNameMember) }.Where(part => part is not null));
        return new OctorateBillingData
        {
            IsCompany = company is not null || vat is not null,
            Name = company ?? (person.Length > 0 ? person : null),
            FiscalCode = fiscalCode,
            VatNumber = vat,
            Address = address,
            PostalCode = FirstOf(record, PostalCodeMembers),
            City = FirstOf(record, CityMembers),
            Province = FirstOf(record, ProvinceMembers),
            CountryCode = FirstOf(record, CountryMembers),
            SdiRecipientCode = sdi,
            Pec = pec,
        };
    }

    /// <summary>
    /// The body written to the customer record: the members of <see cref="WrittenMembers"/> that the data has, in a fixed order, and nothing else.
    /// A member the data lacks is left out (it never clears what Octorate holds). The company name only for a company.
    /// </summary>
    public static string WriteBody(OctorateBillingData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var body = new JsonObject();
        if (data.IsCompany)
            Add(body, CompanyNameMembers[0], data.Name);

        Add(body, FiscalCodeMembers[0], data.FiscalCode);
        Add(body, VatNumberMembers[0], data.VatNumber);
        Add(body, AddressMembers[0], data.Address);
        Add(body, PostalCodeMembers[0], data.PostalCode);
        Add(body, CityMembers[0], data.City);
        Add(body, ProvinceMembers[0], data.Province);
        Add(body, CountryMembers[0], data.CountryCode);
        Add(body, SdiRecipientCodeMembers[0], data.SdiRecipientCode);
        Add(body, PecMembers[0], data.Pec);
        return body.ToJsonString();
    }

    /// <summary>True when <paramref name="data"/> has at least one member to write (a person's name alone has none: it is never written).</summary>
    public static bool HasMembersToWrite(OctorateBillingData data) => WriteBody(data) != EmptyBody;

    /// <summary>
    /// The hash of what <paramref name="data"/> writes (SHA-256 of the contract version and the body, in lower-case hex): the same hash means the
    /// same body, so the data is not written again. Of personal data: it is stored encrypted and never logged.
    /// </summary>
    public static string HashOf(OctorateBillingData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var text = $"v{ContractVersion}\n{WriteBody(data)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static void Add(JsonObject body, string member, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[member] = value.Trim();
    }

    /// <summary>The first of <paramref name="members"/> that <paramref name="element"/> has with a text (a number is read as its text).</summary>
    private static string? FirstOf(JsonElement element, string[] members)
    {
        foreach (var member in members)
        {
            if (OctorateHttp.StringOf(element, member) is { } text)
                return text;
        }

        return null;
    }
}
