using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E06/F03/US03/T01 (FIS-07, NF-87): the SQL the export of the fiscal documents becomes on the real PostgreSQL provider (Npgsql; nothing is executed, see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory accepts what Npgsql cannot turn into SQL, and no integration test of CI calls this service: this is where
/// it is proved that the three queries of the export translate (the documents, the bookings of the rows, the payments of those bookings), that the org and
/// the reach of the caller are in the statement of the documents and not made after it, that the XML and the rest of the recipient are never read, and that
/// the property of a booking is read even when it was deleted.
/// </summary>
public class FiscalDocumentExportNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid BookingId = Guid.Parse("9e8d7c6b-5a49-4382-b1a0-f9e8d7c6b5a4");
    private static readonly Guid PropertyId = Guid.Parse("2b6c8f10-3d4e-4a59-9c7b-1e0d2f3a4b55");

    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly HostScope Restricted = new(OrgId, GrantedToUserId: "auth0|collaboratore");

    [Fact]
    public async Task Export_EveryQueryTranslates_TheOrgTheScopeAndTheLimitAreInTheStatement_AndTheXmlIsNeverRead()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: Rows);
        var criteria = new FiscalDocumentCriteria(
            BookingId,
            PropertyId,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            DocumentKind.Invoice,
            FiscalDocumentStatus.Draft,
            FiscalDocumentSource.CasaZen);

        var rows = await Service(db).ListForExportAsync(Restricted, criteria, 500);

        Assert.Equal(["FiscalDocuments", "Bookings", "Payments", "Bookings"], statements.Select(MainTable));
        var row = Assert.Single(rows);
        Assert.Equal(("ABCDEFGHJK", "Trullo"), (row.BookingCode, row.PropertyName));
        Assert.Equal(FiscalDocumentPaymentState.ToCollect, row.PaymentState);

        var documents = statements[0];
        Assert.Contains("\"OrgId\"", documents, StringComparison.Ordinal);
        Assert.Contains("LIMIT", documents, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", documents, StringComparison.Ordinal);

        Assert.Contains("\"PropertyMemberAccesses\"", documents, StringComparison.Ordinal);
        Assert.Contains("EXISTS", documents, StringComparison.Ordinal);

        Assert.Contains("\"FiscalDocumentLines\"", documents, StringComparison.Ordinal);
        Assert.Contains("SUM(", documents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"LineType\"", documents, StringComparison.Ordinal);
        foreach (var never in new[] { "\"FatturaPaXml\"", "\"RecipientFiscalCode\"", "\"RecipientVatNumber\"", "\"RecipientAddressStreet\"", "\"RecipientPec\"" })
            Assert.DoesNotContain(never, documents, StringComparison.Ordinal);

        var bookings = statements[1];
        Assert.Contains("\"OrgId\"", bookings, StringComparison.Ordinal);
        Assert.Contains("ANY", bookings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IsDeleted\"", bookings, StringComparison.Ordinal);

        var channel = statements[3];
        Assert.Contains("\"OtaPaidAmount\"", channel, StringComparison.Ordinal);
        Assert.Contains("\"OtaCompanyCollect\"", channel, StringComparison.Ordinal);
        Assert.Contains("ANY", channel, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IsDeleted\"", channel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ForTheWholeOrg_HasNoGrantInTheStatement_AndTheOrgIsStillThere()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: Rows);

        await Service(db).ListForExportAsync(OrgWide, new FiscalDocumentCriteria(), 500);

        var documents = statements[0];
        Assert.Equal("FiscalDocuments", MainTable(documents));
        Assert.Contains("\"OrgId\"", documents, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PropertyMemberAccesses\"", documents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WithNoDocument_ReadsNothingElse()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        Assert.Empty(await Service(db).ListForExportAsync(Restricted, new FiscalDocumentCriteria(), 500));

        Assert.Equal(["FiscalDocuments"], statements.Select(MainTable));
    }

    private static FiscalDocumentService Service(AppDbContext db) => new(
        db,
        Mock.Of<IDocumentIssuerService>(),
        Mock.Of<IInvoiceDataService>(),
        Mock.Of<IFiscalNumberingService>(),
        Mock.Of<IFatturaPaXmlBuilder>(),
        Mock.Of<IDomainEventRecorder>(),
        Mock.Of<IPdfDocumentRenderer>(),
        new ResxDocumentTexts(),
        Mock.Of<IFiscalTaxRuleResolver>(),
        new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero)),
        NullLogger<FiscalDocumentService>.Instance);

    private static string MainTable(string sql) =>
        Regex.Match(sql, "^FROM\\s+\"(?<table>\\w+)\"", RegexOptions.Multiline).Groups["table"].Value;

    private static DataTable? Rows(string sql) => MainTable(sql) switch
    {
        "Bookings" when sql.Contains("\"OtaPaidAmount\"", StringComparison.Ordinal) => null,
        "FiscalDocuments" => Table(
            [
                ("Id", typeof(Guid)), ("BookingId", typeof(Guid)), ("Kind", typeof(int)), ("Number", typeof(string)), ("IssueDate", typeof(DateOnly)),
                ("RecipientName", typeof(string)), ("TaxableBase", typeof(decimal)), ("VatAmount", typeof(decimal)), ("StampDuty", typeof(decimal)),
                ("Total", typeof(decimal)), ("Currency", typeof(string)), ("PaymentState", typeof(int)), ("SdiStatus", typeof(string)), ("Source", typeof(int)), ("PaymentChannel", typeof(int)),
                ("Purpose", typeof(int)), ("PaidExtras", typeof(decimal)),
            ],
            Guid.NewGuid(), BookingId, (int)DocumentKind.Invoice, "2026/1", new DateOnly(2026, 10, 5), "TEST Cliente Srl", 330.18m, 31.82m, 0m, 362m, "EUR",
            (int)FiscalDocumentPaymentState.Unknown, FiscalDocumentSdiStatuses.ToSend, (int)FiscalDocumentSource.CasaZen, (int)FiscalPaymentChannel.CasaZen, (int)FiscalDocumentPurpose.GuestStay, 0m),
        "Bookings" => Table([("Id", typeof(Guid)), ("BookingCode", typeof(string)), ("Name", typeof(string))], BookingId, "ABCDEFGHJK", "Trullo"),
        _ => null,
    };

    private static DataTable Table((string Name, Type Type)[] columns, params object[] values)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
            table.Columns.Add(name, type);
        table.Rows.Add(values);
        return table;
    }
}
