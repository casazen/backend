using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The tax reporting of the owners and the tourist tax at year end (E18/F01/US02, NF-117) on the PostgreSQL provider with no server (see <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a
/// query the way .NET does and accepts what Npgsql cannot turn into SQL, and these reads go through a list of ids, a projection to a record with a correlated sum, a read across orgs with the
/// tenant filter ignored and the one definition of an Italian rule as an expression: a query that has no translation fails here, before the CI, and not as a 500 of an export or of the owner
/// portal. The probe answers the first reads with one row so that the ones that follow them are compiled too.
/// </summary>
public class TaxReportingNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("5c1a2f70-3d4e-4b6a-9e18-7a2c3d4e5f60");
    private static readonly Guid OwnerId = Guid.Parse("61111111-2222-3333-4444-555555555555");
    private static readonly Guid OwnerUserId = Guid.Parse("62222222-3333-4444-5555-666666666666");
    private static readonly Guid PropertyId = Guid.Parse("63333333-4444-5555-6666-777777777777");
    private static readonly Guid BookingId = Guid.Parse("64444444-5555-6666-7777-888888888888");
    private static readonly Casazen.Tests.Unit.FakeTimeProvider Clock = new(new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero));

    private const RegexOptions Compiled = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // The statements that need a row to let the code go on: the first of a call, whose answer the next statements are built from.
    private static readonly Regex OwnershipSelect =
        new(@"^SELECT\s+\w+\.""OrgId"",\s*\w+\.""PropertyOwnerId"",\s*\w+\.""PropertyId"",\s*\w+\.""OwnershipStart"",\s*\w+\.""OwnershipEnd""\s+FROM ""PropertyOwnerships""", Compiled);

    private static readonly Regex SwissPropertySelect =
        new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""Name"",\s*\w+\.""Address"",\s*\w+\.""City"",\s*\w+\.""PostalCode"",\s*\w+\.""SwissMunicipalityCode"",\s*\w+\.""Currency"",\s*\w+\.""IsDeleted""\s+FROM ""Properties""", Compiled);

    // The localities of the postal code of a property with the municipality each lies in: the read of the one resolver of the place (E29/F01/US01/T02).
    private static readonly Regex LocalitySelect =
        new(@"^SELECT\s+\w+\.""PostalCode"",\s*\w+\.""NormalizedName"",\s*\w+\.""MunicipalityCode"",\s*\w+\.""OfficialName"",\s*\w+\.""CantonCode""\s+FROM ""SwissLocalities""", Compiled);

    private static readonly Regex SwissStaySelect =
        new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""PropertyId"",\s*\w+\.""CheckInDate"",\s*\w+\.""CheckOutDate""\s+FROM ""Bookings""", Compiled);

    private static readonly Regex StayTaxSelect =
        new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""BookingId"",\s*\w+\.""ConfirmedAt"" IS NOT NULL AS ""Confirmed"",\s*\w+\.""Status"",\s*\w+\.""Collector"",\s*\w+\.""DeclaredCollector""\s+FROM ""StayTouristTaxes""", Compiled);

    /// <summary>A table of one row: the columns are read by their position, in the order of the SELECT of the statement.</summary>
    private static DataTable Table(params (string Name, Type Type, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type, _) in columns)
            table.Columns.Add(name, type);

        table.Rows.Add(columns.Select(column => column.Value).ToArray());
        return table;
    }

    /// <summary>The ownership of the caller of the portal: its access is resolved, so the reads of its withholding are sent too.</summary>
    private static DataTable? OwnershipOf(string sql) =>
        OwnershipSelect.IsMatch(sql.TrimStart())
            ? Table(
                ("OrgId", typeof(Guid), OrgId),
                ("PropertyOwnerId", typeof(Guid), OwnerId),
                ("PropertyId", typeof(Guid), PropertyId),
                ("OwnershipStart", typeof(DateTime), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                ("OwnershipEnd", typeof(DateTime), DBNull.Value))
            : null;

    /// <summary>
    /// A property in Switzerland at 6900 Lugano whose stored municipality number the registry does not have, and a stay of it: the reads of the stored
    /// municipality (no row), of the localities of its postal code (the place is derived: Ticino), of the stays and of their documents are sent too.
    /// </summary>
    private static DataTable? TicinoRowsOf(string sql)
    {
        var text = sql.TrimStart();
        if (SwissPropertySelect.IsMatch(text))
        {
            return Table(
                ("Id", typeof(Guid), PropertyId),
                ("Name", typeof(string), "TEST Casa Lugano"),
                ("Address", typeof(string), "Via Test 1"),
                ("City", typeof(string), "Lugano"),
                ("PostalCode", typeof(string), "6900"),
                ("SwissMunicipalityCode", typeof(string), "9999"),
                ("Currency", typeof(string), "CHF"),
                ("IsDeleted", typeof(bool), false));
        }

        if (LocalitySelect.IsMatch(text))
        {
            return Table(
                ("PostalCode", typeof(string), "6900"),
                ("NormalizedName", typeof(string), "lugano"),
                ("MunicipalityCode", typeof(string), "5192"),
                ("OfficialName", typeof(string), "Lugano"),
                ("CantonCode", typeof(string), "TI"));
        }

        return SwissStaySelect.IsMatch(text)
            ? Table(
                ("Id", typeof(Guid), BookingId),
                ("PropertyId", typeof(Guid), PropertyId),
                ("CheckInDate", typeof(DateTime), new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
                ("CheckOutDate", typeof(DateTime), new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc)))
            : null;
    }

    /// <summary>The tax of a stay, confirmed: the read of its guests is sent too.</summary>
    private static DataTable? StayTaxOf(string sql) =>
        StayTaxSelect.IsMatch(sql.TrimStart())
            ? Table(
                ("Id", typeof(Guid), Guid.NewGuid()),
                ("BookingId", typeof(Guid), BookingId),
                ("Confirmed", typeof(bool), true),
                ("Status", typeof(int), 0),
                ("Collector", typeof(int), DBNull.Value),
                ("DeclaredCollector", typeof(int), DBNull.Value))
            : null;

    private static WithholdingService Withholding(AppDbContext db) => new(db, Clock);

    private static TouristTaxSummaryService TouristTax(AppDbContext db)
    {
        var fiscal = new FiscalService(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), Clock);
        return new TouristTaxSummaryService(db, fiscal, Mock.Of<IFileStorage>(), NullLogger<TouristTaxSummaryService>.Instance, Clock);
    }

    private static OwnerPortalService Portal(AppDbContext db) =>
        new(db, Clock, null, new TestFeatureFlags(FeatureFlags.AgencyTier, FeatureFlags.TaxReportingExports), new OwnerPortalAccessResolver(db, Clock));

    /// <summary>The reads of the tourist tax of a stay (its record, then its guests): a method of the service that only the year-end declaration calls.</summary>
    private static Task StayFacts(AppDbContext db)
    {
        var method = typeof(TouristTaxSummaryService).GetMethod("StayFactsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(TouristTax(db), [OrgId, new List<Guid> { BookingId }, CancellationToken.None])!;
    }

    /// <summary>What a call runs, how many statements it sends at least (all of them are reached when nothing was swallowed early) and the rows that let it go on.</summary>
    private sealed record Call(Func<AppDbContext, Task> Run, int MinimumStatements, Func<string, DataTable?>? Rows = null);

    private static readonly string[] CallNames =
    [
        // The annual Certificazione Unica data (E18/F01/US02/T01): the evidence of the year, the ownerships and the mandates of the properties, the owners, their profiles and the properties.
        "cu-evidence",
        "cu-reporting-owners",
        "cu-people",
        "cu-stored",

        // The 770 and the communication of the short-let contracts (E18/F01/US02/T02): the evidence of the year as an Italian rule, and the decisions with their reversals.
        "tax-770",
        "contracts-communication",

        // The owner portal: the access, then the evidence of the owner profiles, the agencies and the annual data of each.
        "owner-withholding",

        // The tourist tax at year end (E18/F01/US02/T03): the properties of Ticino, their place (E29/F01/US01/T02), their stays and the documents; the record of a stay and its guests.
        "ticino-count",
        "stay-facts",
    ];

    private static Call CallOf(string name) => name switch
    {
        "cu-evidence" => new(db => CuDatasetBuilder.BuildAsync(db, OrgId, 2026, CancellationToken.None), 1),
        "cu-reporting-owners" => new(db => ReportingOwners.ReadAsync(db, OrgId, [PropertyId], CancellationToken.None), 2),
        "cu-people" => new(db => TaxReportingPeople.ReadAsync(db, OrgId, [OwnerId], [PropertyId], CancellationToken.None), 3),
        "cu-stored" => new(db => Withholding(db).GetCuDatasetAsync(OrgId, 2026), 1),
        "tax-770" => new(db => Withholding(db).Get770DataAsync(OrgId, 2026), 1),
        "contracts-communication" => new(db => Withholding(db).GetContractsCommunicationAsync(OrgId, 2026), 1),

        // The ownership of the caller, its delegations, the evidence of its owner profile, the agencies and the annual data.
        "owner-withholding" => new(db => Portal(db).GetOwnerWithholdingAsync(OwnerUserId, 2026), 4, OwnershipOf),

        // The properties in Switzerland, their stored municipalities, the localities of their postal codes, the stays of the year and the documents issued for them.
        "ticino-count" => new(db => TouristTax(db).GetTicinoYearlyCountAsync(new HostScope(OrgId), 2026), 5, TicinoRowsOf),
        "stay-facts" => new(StayFacts, 2, StayTaxOf),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "A call of the list that has no definition."),
    };

    public static TheoryData<string> EveryCall()
    {
        var data = new TheoryData<string>();
        foreach (var name in CallNames)
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string name)
    {
        var call = CallOf(name);
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: call.Rows);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => call.Run(db));

        // The probe must have got as far as the last query of the call, or it proved nothing about the ones after the first.
        Assert.True(
            statements.Count >= call.MinimumStatements,
            $"{name}: only {statements.Count} of the expected {call.MinimumStatements} statements were reached:{Environment.NewLine}{string.Join(Environment.NewLine + "--" + Environment.NewLine, statements)}");
    }
}
