using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data of the tests of the wire files (E06/F05/US01/T01, E16/F01/US01/T01): an org with its issuer profile, owners with an address and an IBAN, published
/// statement versions and the confirmed payouts a batch is made of, in an in-memory database. Everything is invented; the IBANs are valid examples (check digits
/// included) and none of them is real. <see cref="Secrets"/> is what identifies a person or an account in them: none of it may reach an error, an exception or a
/// log line.
/// </summary>
internal static class WireTestData
{
    /// <summary>A valid Swiss IBAN (a normal account, institution 00762).</summary>
    public const string SwissIban = "CH9300762011623852957";

    /// <summary>Another valid Swiss IBAN.</summary>
    public const string OtherSwissIban = "CH5604835012345678009";

    /// <summary>A valid Liechtenstein IBAN: a domestic payment of the Swiss system.</summary>
    public const string LiechtensteinIban = "LI21088100002324013AA";

    /// <summary>A valid QR-IBAN (institution 31999): the account of a QR-bill, never the creditor account of a normal transfer.</summary>
    public const string QrIban = "CH4431999123000889012";

    /// <summary>A valid Italian IBAN.</summary>
    public const string ItalianIban = "IT60X0542811101000000123456";

    /// <summary>Another valid Italian IBAN.</summary>
    public const string OtherItalianIban = "IT41W8000000292100645211151";

    /// <summary>An Italian IBAN of 26 characters (Italy has 27) whose check digits are right: the checksum alone cannot tell, the length of the country does.</summary>
    public const string WrongLengthItalianIban = "IT90X054281110100000012345";

    /// <summary>The IBAN of the agency, on its issuer profile: a valid Swiss IBAN.</summary>
    public const string DebitIban = "CH4308307000289537312";

    /// <summary>A Swiss IBAN whose check digits do not match.</summary>
    public const string BadIban = "CH9300762011623852958";

    /// <summary>Everything that identifies a person or an account in these tests.</summary>
    public static readonly string[] Secrets =
    [
        SwissIban, OtherSwissIban, LiechtensteinIban, QrIban, ItalianIban, OtherItalianIban, WrongLengthItalianIban, DebitIban, BadIban,
        "Mario", "Rossi", "Luigi", "Bianchi", "Giulia", "Verdi", "Quokka Immobiliare", "Via Nassa", "Via Cantonale",
    ];

    /// <summary>The instant of the tests: 14 November 2026, 10:00 UTC (11:00 in Rome).</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The execution date the tests ask the bank for: two days after <see cref="Now"/>.</summary>
    public static readonly DateOnly ExecutionDate = new(2026, 11, 16);

    public static FakeTimeProvider Clock()
    {
        return new FakeTimeProvider(Now);
    }

    public static DbContextOptions<AppDbContext> NewOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Wire_{Guid.NewGuid()}").Options;
    }

    /// <summary>A context of <paramref name="orgId"/> with the encrypted columns on (<paramref name="provider"/>, or an ephemeral one).</summary>
    public static AppDbContext NewDb(Guid orgId, bool filterEnabled = true, DbContextOptions<AppDbContext>? options = null, IDataProtectionProvider? provider = null)
    {
        return new AppDbContext(options ?? NewOptions(), new WireTestTenant(orgId, filterEnabled), provider ?? new EphemeralDataProtectionProvider());
    }

    /// <summary>The service of the tests: a schema of its own that accepts any content (the official ones are not in the repository), and the clock of <see cref="Now"/>.</summary>
    public static WireTransferService NewService(AppDbContext db, ILogger<WireTransferService>? logger = null, Func<string, System.Xml.Schema.XmlSchemaSet>? schemas = null)
    {
        return new WireTransferService(db, logger ?? NullLogger<WireTransferService>.Instance, schemas ?? (_ => Pain001TestSchemas.Accepting()), Clock());
    }

    /// <summary>The issuer profile of the agency: a Swiss company with its address and the IBAN that pays.</summary>
    public static async Task AddIssuerAsync(AppDbContext db, Guid orgId, string? iban = DebitIban, string? legalName = "Quokka Immobiliare SA", string? street = "Via Nassa")
    {
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            LegalName = legalName,
            AddressStreet = street,
            AddressCivic = "5",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
            Iban = iban,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An owner with an IBAN and, unless <paramref name="withAddress"/> is false, a complete structured address in Lugano.</summary>
    public static async Task<Guid> AddOwnerAsync(
        AppDbContext db,
        Guid orgId,
        string firstName,
        string lastName,
        string? iban,
        bool withAddress = true,
        string? legalName = null)
    {
        var owner = new PropertyOwner
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = firstName,
            LastName = lastName,
            LegalName = legalName,
            Email = $"{Guid.NewGuid():N}@example.com",
            Iban = iban,
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow,
            BillingStreet = withAddress ? "Via Cantonale" : null,
            BillingCivic = withAddress ? "12" : null,
            BillingPostalCode = withAddress ? "6900" : null,
            BillingCity = withAddress ? "Lugano" : null,
            BillingCountry = withAddress ? "CH" : null,
        };
        db.PropertyOwners.Add(owner);
        await db.SaveChangesAsync();
        return owner.Id;
    }

    /// <summary>
    /// A confirmed payout of <paramref name="ownerId"/> (<paramref name="net"/> after a withholding of <paramref name="withholding"/>) for a statement version of
    /// October 2026, published unless <paramref name="published"/> is false.
    /// </summary>
    public static async Task<Guid> AddPayoutAsync(
        AppDbContext db,
        Guid orgId,
        Guid ownerId,
        decimal net,
        string currency = "CHF",
        decimal withholding = 0m,
        bool published = true,
        OwnerPayoutStatus status = OwnerPayoutStatus.Confirmed)
    {
        var now = Now.UtcDateTime;
        var version = new OwnerStatementVersion
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PeriodYear = 2026,
            PeriodMonth = 10,
            VersionNumber = (await db.OwnerStatementVersions.IgnoreQueryFilters().CountAsync(v => v.PropertyOwnerId == ownerId)) + 1,
            Status = published ? OwnerStatementVersionStatus.Published : OwnerStatementVersionStatus.Approved,
            Currency = currency,
            CutoffAt = now,
            EntriesSha256 = new string('a', OwnerStatementVersion.HashLength),
            PublishedAt = published ? now : null,
            CreatedAt = now,
        };
        var payout = new OwnerPayout
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            OwnerStatementVersionId = version.Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PaidOn = DateOnly.FromDateTime(now),
            Currency = currency,
            GrossAmount = net + withholding,
            WithholdingAmount = withholding,
            NetAmount = net,
            Status = status,
            ConfirmedAt = now,
        };
        db.OwnerStatementVersions.Add(version);
        db.OwnerPayouts.Add(payout);
        await db.SaveChangesAsync();
        return payout.Id;
    }

    /// <summary>Two owners of Lugano with a Swiss IBAN and a payout each: 1200.00 and 1574.50 in CHF. Returns the payout ids.</summary>
    public static async Task<List<Guid>> AddTwoPayoutsAsync(AppDbContext db, Guid orgId, string currency = "CHF")
    {
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", SwissIban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherSwissIban);
        return
        [
            await AddPayoutAsync(db, orgId, mario, 1200.00m, currency),
            await AddPayoutAsync(db, orgId, luigi, 1574.50m, currency),
        ];
    }

    public static async Task AssertNothingSavedAsync(AppDbContext db)
    {
        Assert.Empty(await db.WireBatches.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.WireBatchPayouts.IgnoreQueryFilters().ToListAsync());
    }
}

/// <summary>A tenant context of the tests: the org the context is of, and whether the global filter is on.</summary>
internal sealed class WireTestTenant(Guid orgId, bool filterEnabled = true) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => filterEnabled;
}
