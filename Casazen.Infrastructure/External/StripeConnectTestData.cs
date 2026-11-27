using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stripe's published test values for the onboarding of a Connect account in <b>test mode</b>
/// (<c>https://docs.stripe.com/connect/testing-verification</c>), per country. In test mode Stripe checks identities against
/// nothing but these values: the date of birth <c>1901-01-01</c> and the address line <c>address_full_match</c> are the ones that
/// match, the test phone number is <c>0000000000</c>, the test bank accounts are Stripe's test IBANs. Any other value, above all
/// a real name, birth date or address typed into the hosted form, fails the identity check
/// (<c>verification_failed_keyed_identity</c>) and the account never leaves the onboarding (issue #1001, the Quokka360 test account).
/// <para>
/// An account created with them as an <c>individual</c> has only the acceptance of the Stripe terms left (checked on Stripe's test
/// mode for <c>IT</c> and <c>CH</c>: <c>requirements.currently_due</c> is <c>tos_acceptance.date</c> and <c>tos_acceptance.ip</c>,
/// the identity fields pass verification). They are <b>public Stripe test fixtures, not personal data</b>, and are sent only with a
/// test-mode key (<see cref="StripeConnectGateway"/>).
/// </para>
/// <para>
/// No <c>business_profile.url</c> is sent: Stripe validates it even in test mode and refuses reserved domains, which would fail the
/// whole update; the product description satisfies the requirement.
/// </para>
/// </summary>
/// <param name="Phone">The test phone number of the country (a valid number of its numbering plan made of zeros).</param>
/// <param name="City">A real city of the country: Stripe wants legitimate city, state and postal code around the address token.</param>
/// <param name="State">The province or canton code of <paramref name="City"/>.</param>
/// <param name="PostalCode">The postal code of <paramref name="City"/>.</param>
/// <param name="Iban">Stripe's test IBAN of the country.</param>
/// <param name="Currency">The currency of the bank account (lower case).</param>
/// <param name="IdNumber">The test personal id number, for the countries whose individual accounts ask for one (Italy); <c>null</c> otherwise.</param>
internal sealed record StripeConnectTestData(
    string Country,
    string Phone,
    string City,
    string State,
    string PostalCode,
    string Iban,
    string Currency,
    string? IdNumber)
{
    /// <summary>The address line that matches in Stripe's test verification.</summary>
    public const string MatchingAddressLine = "address_full_match";

    /// <summary>Merchant category code of the account: real estate agents and managers, rentals.</summary>
    public const string MerchantCategoryCode = "6513";

    public const string ProductDescription = "Short-term rentals of apartments (CasaZen test account)";

    private const string FirstName = "Test";
    private const string LastName = "Host";

    private static readonly StripeConnectTestData Italy = new(
        "IT",
        Phone: "+390000000000",
        City: "Milano",
        State: "MI",
        PostalCode: "20121",
        Iban: "IT60X0542811101000000123456",
        Currency: "eur",
        IdNumber: "000000000");

    private static readonly StripeConnectTestData Switzerland = new(
        "CH",
        Phone: "+410000000000",
        City: "Lugano",
        State: "TI",
        PostalCode: "6900",
        Iban: "CH9300762011623852957",
        Currency: "chf",
        IdNumber: null);

    /// <summary>The test data of <paramref name="country"/> (ISO 3166-1 alpha-2, upper case); <c>null</c> for a country without any: its test account then keeps the empty hosted onboarding.</summary>
    public static StripeConnectTestData? ForCountry(string? country) => country switch
    {
        "IT" => Italy,
        "CH" => Switzerland,
        _ => null,
    };

    /// <summary>
    /// The one update that fills a new Express account: an individual with the matching test identity and a test bank account.
    /// <paramref name="email"/> is the one the account was created with.
    /// </summary>
    public AccountUpdateOptions ToUpdateOptions(string? email)
    {
        var individual = new AccountIndividualOptions
        {
            FirstName = FirstName,
            LastName = LastName,
            Phone = Phone,
            Dob = new DobOptions { Day = 1, Month = 1, Year = 1901 },
            Address = new AddressOptions
            {
                Line1 = MatchingAddressLine,
                City = City,
                State = State,
                PostalCode = PostalCode,
                Country = Country,
            },
            IdNumber = IdNumber,
        };

        if (!string.IsNullOrWhiteSpace(email))
            individual.Email = email.Trim();

        var options = new AccountUpdateOptions
        {
            BusinessType = "individual",
            BusinessProfile = new AccountBusinessProfileOptions
            {
                Mcc = MerchantCategoryCode,
                ProductDescription = ProductDescription,
            },
            Individual = individual,
        };

        // A bank account given as a dictionary, as the Stripe API documents it (the typed option only takes a token).
        options.AddExtraParam("external_account[object]", "bank_account");
        options.AddExtraParam("external_account[country]", Country);
        options.AddExtraParam("external_account[currency]", Currency);
        options.AddExtraParam("external_account[account_number]", Iban);
        return options;
    }
}
