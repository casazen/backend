using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The checks of the limit of units and of the company tenant (E18/F01/US03/T01) run on the PostgreSQL provider with all three kinds of scope and no server (see
/// <see cref="NpgsqlTranslationProbe"/>): InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL, and these services read through the reach
/// of the caller (<c>InScope</c>, a sub-query of the reachable properties), through a list of ids, a sub-query of the invoice data and a projection to a record. The probe answers
/// the first queries with one row so the ones that follow are compiled too; this fails here, before CI, when one has no translation.
/// </summary>
public class OwnerTaxChecksNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid OwnerId = Guid.Parse("21111111-2222-3333-4444-555555555555");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid BookingId = Guid.Parse("31111111-2222-3333-4444-555555555555");
    private static readonly Casazen.Tests.Unit.FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 11, 15);

    public static TheoryData<string> Scopes => ["collaborator", "account-in-no-team", "whole-org"];

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static OwnerTaxChecks Checks(AppDbContext db) => new(
        db,
        new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
        new TestFeatureFlags(FeatureFlags.TaxReportingExports),
        Microsoft.Extensions.Options.Options.Create(new ShortStayFiscalOptions()),
        Clock,
        NullLogger<OwnerTaxChecks>.Instance);

    /// <summary>
    /// What the probe answers with one row so the code goes on: the owner (id, kind, regime, declared units), the ownerships of the owner on a property of its own (with and without
    /// the join to the property), the booking of a company, and a «true» for the existence of an owner.
    /// </summary>
    private static DataTable? Rows(string sql)
    {
        var text = sql.TrimStart();
        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        if (Regex.IsMatch(text, @"^SELECT\s+EXISTS\b", Ci) && text.Contains("FROM \"PropertyOwners\"", StringComparison.Ordinal))
        {
            var exists = new DataTable();
            exists.Columns.Add("value", typeof(bool));
            exists.Rows.Add(true);
            return exists;
        }

        if (Regex.IsMatch(text, @"^SELECT\s+\w+\.""Id"",\s*\w+\.""EntityKind"",\s*\w+\.""TaxRegime"",\s*\w+\.""ExternalShortLetUnits""", Ci))
        {
            var owner = new DataTable();
            owner.Columns.Add("Id", typeof(Guid));
            owner.Columns.Add("EntityKind", typeof(int));
            owner.Columns.Add("TaxRegime", typeof(int));
            owner.Columns.Add("ExternalShortLetUnits", typeof(int));
            owner.Rows.Add(OwnerId, 1, 0, 1);
            return owner;
        }

        if (Regex.IsMatch(text, @"^SELECT\s+\w+\.""Id"",\s*\w+\.""EntityKind"",\s*\w+\.""TaxRegime""\s+FROM", Ci))
        {
            var owner = new DataTable();
            owner.Columns.Add("Id", typeof(Guid));
            owner.Columns.Add("EntityKind", typeof(int));
            owner.Columns.Add("TaxRegime", typeof(int));
            owner.Rows.Add(OwnerId, 1, 0);
            return owner;
        }

        if (Regex.IsMatch(text, @"^SELECT\s+\w+\.""PropertyOwnerId"",\s*\w+\.""PropertyId"",\s*\w+\.""OwnershipStart"",\s*\w+\.""OwnershipEnd""", Ci)
            || Regex.IsMatch(text, @"^SELECT\s+\w+\.""PropertyId"",\s*\w+\.""PropertyOwnerId"",\s*\w+\.""OwnershipStart"",\s*\w+\.""OwnershipEnd""", Ci))
        {
            // The units read the owner first, the stay reads the property first: the columns follow the order of the select.
            var ownerFirst = Regex.IsMatch(text, @"^SELECT\s+\w+\.""PropertyOwnerId""", Ci);
            var periods = new DataTable();
            periods.Columns.Add(ownerFirst ? "PropertyOwnerId" : "PropertyId", typeof(Guid));
            periods.Columns.Add(ownerFirst ? "PropertyId" : "PropertyOwnerId", typeof(Guid));
            periods.Columns.Add("OwnershipStart", typeof(DateTime));
            periods.Columns.Add("OwnershipEnd", typeof(DateTime));
            periods.Rows.Add(ownerFirst ? OwnerId : PropertyId, ownerFirst ? PropertyId : OwnerId, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), DBNull.Value);
            return periods;
        }

        if (Regex.IsMatch(text, @"^SELECT\s+\w+\.""Id"",\s*\w+\.""BookingCode""", Ci))
        {
            var booking = new DataTable();
            booking.Columns.Add("Id", typeof(Guid));
            booking.Columns.Add("BookingCode", typeof(string));
            booking.Columns.Add("PropertyId", typeof(Guid));
            booking.Columns.Add("Name", typeof(string));
            booking.Columns.Add("Country", typeof(string));
            booking.Columns.Add("Status", typeof(int));
            booking.Columns.Add("NoShowAt", typeof(DateTime));
            booking.Columns.Add("CheckInDate", typeof(DateTime));
            booking.Columns.Add("CheckOutDate", typeof(DateTime));
            booking.Rows.Add(BookingId, "CZ-TEST01", PropertyId, "TEST Casa", "IT", 1, DBNull.Value, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc));
            return booking;
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task TheLimitOfUnits_TranslatesToSql_ForEveryScope(string scopeKind)
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, rows: Rows);
        var checks = Checks(db);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.CheckUnitsAsync(OrgId, [OwnerId], Today));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.GetUnitsCheckAsync(ScopeOf(scopeKind), OwnerId));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.DeclareExternalUnitsAsync(ScopeOf(scopeKind), OwnerId, 1, "auth0|admin"));

        // The probe swallows what is not a translation failure, so say that it went as far as the queries of the owners and of the ownerships.
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwners\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwnerships\"", StringComparison.Ordinal) && s.Contains("\"Properties\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task TheCompanyTenant_TranslatesToSql_ForEveryScope(string scopeKind)
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, rows: Rows);
        var checks = Checks(db);
        var command = new DecideShortLetTaxCommand { BookingId = BookingId, Regime = ShortLetTaxRegime.NotShortLet, Reason = "TEST" };

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.ListCompanyTenantCasesAsync(ScopeOf(scopeKind), null));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.ListCompanyTenantCasesAsync(ScopeOf(scopeKind), CompanyTenantCaseState.Pending));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.DecideAsync(ScopeOf(scopeKind), command, "auth0|admin"));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.TreatmentOfStaysAsync(OrgId, OwnerId, [BookingId], Today));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => checks.RaiseCompanyTenantDecisionAsync(OrgId, BookingId));

        // As far as the stay, the invoice data of its company, the owners of its property and the decisions of the booking.
        Assert.Contains(statements, s => s.Contains("FROM \"Bookings\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"BookingInvoiceData\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwnerships\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwners\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"ShortLetTaxDecisions\"", StringComparison.Ordinal));
    }
}
