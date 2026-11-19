using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E15/F01/US01/T01: the reads of the accountant package, run on the PostgreSQL provider with the three kinds of scope, without a server (see <see cref="NpgsqlTranslationProbe"/>). The proofs
/// of the package run on InMemory, which evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL; this fails here, before the CI, when the documents of the period
/// (the projection with its lines, the document it credits and the <c>EXISTS</c> on the bookings of the scope) or any other read of the package has no translation.
/// </summary>
public class AccountantPackageNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static readonly AccountantPackagePeriod Year = AccountantPackagePeriod.OfYear(2026);

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    /// <summary>Every read of the package that the web layer reaches through the scope of the caller or the org, by the name the failure reports.</summary>
    private static readonly Dictionary<string, Func<AppDbContext, HostScope, Task>> Calls = new()
    {
        ["guest-documents-count"] = (db, scope) => AccountantPackageFacts.IssuedDocuments(db, scope, Year, FiscalDocumentPurpose.GuestStay).CountAsync(),
        ["guest-documents"] = (db, scope) => AccountantPackageFacts.ReadAsync(db, scope, Year, FiscalDocumentPurpose.GuestStay, CancellationToken.None),
        ["owner-invoices"] = (db, scope) => AccountantPackageFacts.ReadAsync(db, scope, Year, FiscalDocumentPurpose.OwnerInvoice, CancellationToken.None),
        ["self-billed-documents"] = (db, scope) => AccountantPackageFacts.ReadAsync(db, scope, Year, FiscalDocumentPurpose.SelfBilling, CancellationToken.None),
        ["self-billed-with-files-count"] = (db, scope) => AccountantPackageFacts.IssuedSelfBilledWithFiles(db, scope, Year).CountAsync(),
        ["self-billed-suppliers"] = (db, _) => AccountantPackageFacts.SelfBilledSuppliersAsync(db, OrgId, [Guid.NewGuid()], CancellationToken.None),
        ["current-issuer"] = (db, _) => AccountantPackageFacts.CurrentIssuerAsync(db, OrgId, CancellationToken.None),
        ["owner-names"] = (db, _) => AccountantPackageFacts.OwnerNamesAsync(db, OrgId, [Guid.NewGuid()], CancellationToken.None),
        ["swiss-cantons"] = (db, _) => AccountantPackageFacts.SwissCantonsAsync(db, OrgId, [Guid.NewGuid()], CancellationToken.None),
        ["portal-commissions"] = (db, _) => AccountantPackageFacts.PortalCommissionsBorneAsync(db, new PortalAccountHolderResolver(db), OrgId, Year, CancellationToken.None),
        ["portal-commission-lines"] = (db, _) => AccountantPackageFacts.PortalCommissionLinesAsync(db, new PortalAccountHolderResolver(db), OrgId, Year, CancellationToken.None),
    };

    /// <summary>
    /// How many statements each call sends at least (the probe must have reached them, or it proved nothing). The invoices to the owners are read only by a scope that reaches the whole org: for
    /// any other the query is <c>WHERE false</c>, which EF may answer without sending it.
    /// </summary>
    private static readonly Dictionary<string, int> MinimumStatements = new()
    {
        ["guest-documents-count"] = 1,
        ["guest-documents"] = 1,
        ["owner-invoices"] = 0,
        ["self-billed-documents"] = 0,
        ["self-billed-with-files-count"] = 0,
        ["self-billed-suppliers"] = 1,
        ["current-issuer"] = 2,
        ["owner-names"] = 1,
        ["swiss-cantons"] = 1,
        ["portal-commissions"] = 1,
        ["portal-commission-lines"] = 1,
    };

    public static TheoryData<string, string> EveryCallWithEveryScope()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in Calls.Keys)
        {
            foreach (var scope in new[] { "collaborator", "account-in-no-team", "whole-org" })
                data.Add(call, scope);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCallWithEveryScope))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string call, string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](db, ScopeOf(scope)));

        Assert.True(
            statements.Count >= MinimumStatements[call],
            $"{call}: only {statements.Count} of the expected {MinimumStatements[call]} statements were reached.");
    }

    [Fact]
    public void EveryCall_HasItsMinimum()
    {
        Assert.Equal(Calls.Keys.Order(), MinimumStatements.Keys.Order());
    }
}
