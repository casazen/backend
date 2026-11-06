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
/// <item><b>The names are the OpenAPI's, the wire shape is not proven.</b> The record is the <c>GuestDTO</c> of the OpenAPI (<c>GET</c> and
/// <c>PUT /rest/v1/guests/{acc}/{guestId}</c>, whose operations declare neither a body nor an answer: the schema is the only description there is), and
/// the record a reservation belongs to is the <c>invoiceHolderId</c> of the reservation (<see cref="OctorateReservation.InvoiceHolderId"/>). This
/// build could not reach the Octorate sandbox, so no member was read from a real answer: check them with one real read and write, then fix this
/// class and raise <see cref="ContractVersion"/> (<c>docs/runbooks/feature-flags.md</c>, «Before turning <c>OctorateBillingSync</c> on») before
/// <c>Features:OctorateBillingSync</c> is turned on.</item>
/// <item><b>Written</b> (<see cref="WrittenMembers"/>), the writable members of <c>GuestDTO</c> that are billing data: <c>fiscalCode</c>, <c>vatCode</c>,
/// <c>address</c> (one line), <c>zip</c>, <c>city</c>, <c>country</c> and <c>legalMail</c>, the PEC (write-only: Octorate never returns it).
/// <b>Not written until the sandbox proves where they go:</b> the denomination of a company (<c>company</c> is read-only; which writable member holds
/// it is not known), the code of the recipient on the SDI (<c>destinationOffice</c> is read-only), the province (the schema has none, <c>district</c>
/// is not known to be one) and the name of a person (the record already holds the guest's). Never a document, an Alloggiati datum, a phone number or the
/// e-mail address of the guest (<c>mobile</c> and <c>email</c> are members of the record this class never writes).</item>
/// <item><b>Read</b> (the pre-fill): the same members, plus the read-only <c>company</c> and <c>destinationOffice</c> and the person's <c>firstname</c> and
/// <c>lastname</c>. A member that is not there is not an error: the record simply has no such data. The PEC is never read (write-only), nor the province.</item>
/// <item><b>The hash</b> (<see cref="HashOf"/>) is the hash of the body that is written, so it changes when the data does and when this class starts
/// writing something else.</item>
/// </list>
/// </remarks>
internal static class OctorateBillingMapping
{
    /// <summary>Bumped when the shape written below changes, so the data already written is written again in the new shape.</summary>
    public const int ContractVersion = 2;

    /// <summary>The body of data with nothing to write: never sent.</summary>
    public const string EmptyBody = "{}";

    // The members of the record (GuestDTO).
    public const string FiscalCodeMember = "fiscalCode";
    public const string VatNumberMember = "vatCode";
    public const string AddressMember = "address";
    public const string PostalCodeMember = "zip";
    public const string CityMember = "city";
    public const string CountryMember = "country";

    /// <summary>The PEC. Write-only: Octorate answers a flag (<c>legalMailAvailable</c>) and gives the value only through the contact read.</summary>
    public const string PecMember = "legalMail";

    // Read only: the denomination of a company and the code of the recipient on the SDI (probably), never written.
    private const string CompanyMember = "company";
    private const string SdiRecipientCodeMember = "destinationOffice";

    // The name of the person is read to complete the recipient, never written.
    private const string FirstNameMember = "firstname";
    private const string LastNameMember = "lastname";

    /// <summary>Every member <see cref="WriteBody"/> can write, and no other: the whole of what leaves CasaZen.</summary>
    public static IReadOnlyList<string> WrittenMembers { get; } =
    [
        FiscalCodeMember,
        VatNumberMember,
        AddressMember,
        PostalCodeMember,
        CityMember,
        CountryMember,
        PecMember,
    ];

    /// <summary>
    /// The billing data of a customer record, or <c>null</c> when the record holds none: a record needs at least a company name, a fiscal code, a VAT
    /// number, a code of the recipient on the SDI or an address to count (a guest's name alone is not billing data: the invoice data of every imported
    /// booking would be made of it). A company is a record with a company name or a VAT number.
    /// </summary>
    public static OctorateBillingData? Read(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
            return null;

        var company = OctorateJson.Text(record, CompanyMember);
        var vat = OctorateJson.Text(record, VatNumberMember);
        var fiscalCode = OctorateJson.Text(record, FiscalCodeMember);
        var address = OctorateJson.Text(record, AddressMember);
        var sdi = OctorateJson.Text(record, SdiRecipientCodeMember);
        if (company is null && vat is null && fiscalCode is null && address is null && sdi is null)
            return null;

        var person = string.Join(
            ' ',
            new[] { OctorateJson.Text(record, FirstNameMember), OctorateJson.Text(record, LastNameMember) }.Where(part => part is not null));
        return new OctorateBillingData
        {
            IsCompany = company is not null || vat is not null,
            Name = company ?? (person.Length > 0 ? person : null),
            FiscalCode = fiscalCode,
            VatNumber = vat,
            Address = address,
            PostalCode = OctorateJson.Text(record, PostalCodeMember),
            City = OctorateJson.Text(record, CityMember),
            CountryCode = OctorateJson.Text(record, CountryMember),
            SdiRecipientCode = sdi,
        };
    }

    /// <summary>
    /// The body written to the customer record: the members of <see cref="WrittenMembers"/> that the data has, in a fixed order, and nothing else.
    /// A member the data lacks is left out (it never clears what Octorate holds). The country only qualifies the other members: with none of them
    /// there is nothing to write, not even the country.
    /// </summary>
    public static string WriteBody(OctorateBillingData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var others = new[] { data.FiscalCode, data.VatNumber, data.Address, data.PostalCode, data.City, data.Pec };
        if (others.All(string.IsNullOrWhiteSpace))
            return EmptyBody;

        var body = new JsonObject();
        Add(body, FiscalCodeMember, data.FiscalCode);
        Add(body, VatNumberMember, data.VatNumber);
        Add(body, AddressMember, data.Address);
        Add(body, PostalCodeMember, data.PostalCode);
        Add(body, CityMember, data.City);
        Add(body, CountryMember, data.CountryCode);
        Add(body, PecMember, data.Pec);
        return body.ToJsonString();
    }

    /// <summary>True when <paramref name="data"/> has at least one member to write (a name, a province or a country alone have none: they are never written alone).</summary>
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
}
