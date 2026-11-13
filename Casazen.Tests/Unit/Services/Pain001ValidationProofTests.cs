using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F05/US01/T01 (NF-25, AC-E06-F05-US01-01): the wire file is validated before the batch is saved. A file that does not validate leaves no row behind, a
/// file is never produced without its schema, the money of the file is exact (decimals, never a float) and matches its lines, the errors carry no IBAN and no
/// name, and the validation reads no external entity. The official schemas are not in the repository until an operator adds them
/// (docs/runbooks/wire-transfers.md): the tests that need to get past the validation give the service a schema of their own (<see cref="Pain001TestSchemas"/>).
/// </summary>
public class Pain001ValidationProofTests
{
    private const string OwnerIban = "IT60X0542811101000000123456";
    private const string OtherOwnerIban = "IT41W8000000292100645211151";
    private const string DebitIban = "DE89370400440532013000";
    private const string SwissOwnerIban = "CH9300762011623852957";
    private const string BadIban = "IT00X0542811101000000123456";

    private static readonly XNamespace Pain = XNamespace.Get(Pain001SchemaProvider.Namespace);

    // Everything that identifies a person or an account in these tests: none of it may reach an error, an exception or a log line.
    private static readonly string[] Secrets =
        [OwnerIban, OtherOwnerIban, DebitIban, SwissOwnerIban, BadIban, "Mario", "Rossi", "Luigi", "Bianchi", "Giulia", "Verdi"];

    // ---- the proof of the brief ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Money rule: a file that fails the schema is refused (422 <c>wire_batch_xml_invalid</c>) and no batch row is written, not even the batch without its
    /// file the old code left behind. The statements stay free, so a corrected retry makes exactly one batch. The error is the line and position of the
    /// schema errors, with no IBAN and no name, and the log carries counts only.
    /// </summary>
    [Fact]
    public async Task WireBatch_InvalidXml_NeverSaved_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherOwnerIban);
        var first = await AddStatementAsync(db, orgId, mario, 1200.00m);
        var second = await AddStatementAsync(db, orgId, luigi, 1574.50m);
        var logger = new CapturingLogger<WireTransferService>();
        var service = new WireTransferService(db, logger, _ => Pain001TestSchemas.Rejecting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [first, second], DebitIban));

        Assert.Equal(WireTransferErrors.XmlInvalid, refused.Code);
        Assert.Equal("WireBatchXmlInvalid", refused.MessageKey);
        Assert.Equal(2, refused.MessageArgs.Count);
        Assert.Matches(@"^\d+:\d+(, \d+:\d+)*$", (string)refused.MessageArgs[1]);
        await AssertNothingSavedAsync(db);
        AssertNoSecret(refused, logger);

        var retry = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());
        var (batch, _) = await retry.CreateBatchAsync(orgId, [first, second], DebitIban);

        Assert.Single(await db.WireBatches.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(2, (await db.Set<WireBatchStatement>().IgnoreQueryFilters().ToListAsync()).Count);
        Assert.Equal(2774.50m, batch.TotalAmount);
    }

    /// <summary>
    /// With the official schema installed the file CasaZen writes validates against it and an altered one does not; without it nothing is produced (422
    /// <c>wire_batch_schema_unavailable</c>) and nothing is saved: never a file that was not validated, never a made-up success.
    /// </summary>
    [Theory]
    [InlineData("EUR", OwnerIban)]
    [InlineData("CHF", SwissOwnerIban)]
    public async Task WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test(string currency, string ownerIban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ownerIban);
        var statement = await AddStatementAsync(db, orgId, owner, 800.00m, currency);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance);

        if (!Pain001SchemaProvider.HasOfficialSchema(currency))
        {
            var unavailable = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [statement], DebitIban));
            Assert.Equal(WireTransferErrors.SchemaUnavailable, unavailable.Code);
            Assert.Equal("WireBatchSchemaUnavailable", unavailable.MessageKey);
            Assert.Equal(currency, Assert.Single(unavailable.MessageArgs));
            Assert.Null(Pain001SchemaProvider.LoadProblem(currency));
            await AssertNothingSavedAsync(db);
            return;
        }

        var (_, xml) = await service.CreateBatchAsync(orgId, [statement], DebitIban);
        var schemas = Pain001SchemaProvider.GetSchemas(currency);
        Assert.Empty(XsdValidator.Validate(xml, schemas));

        var altered = XDocument.Parse(xml);
        altered.Descendants(Pain + "MsgId").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), schemas));
    }

    /// <summary>The schemas embedded in the build either compile or the reason is known: a build never has files it cannot use and says nothing about it.</summary>
    [Fact]
    public void Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test()
    {
        var names = Pain001SchemaProvider.EmbeddedSchemaNames();

        foreach (var (currency, prefix) in new[]
                 {
                     (WireTransferErrors.SepaCurrency, Pain001SchemaProvider.SepaResourcePrefix),
                     (WireTransferErrors.SwissCurrency, Pain001SchemaProvider.SwissResourcePrefix),
                 })
        {
            var embedded = names.Any(n => n.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(
                Pain001SchemaProvider.HasOfficialSchema(currency) == embedded,
                $"{currency}: embedded files = {embedded}, usable = {Pain001SchemaProvider.HasOfficialSchema(currency)} ({Pain001SchemaProvider.LoadProblem(currency)})");
        }
    }

    /// <summary>
    /// The loader that compiles the official files the day an operator adds them, with stand-in files: a schema of the pain.001 namespace becomes a set that
    /// validates; no file is no set and no reason (the build simply has no schema); a file that does not compile, that has a DTD or that is another schema is no
    /// set and the reason is kept. A broken file is never taken for an installed schema.
    /// </summary>
    [Fact]
    public void Pain001SchemaProvider_Load_CompilesTheFiles_AndSaysWhyWhenItCannot_Test()
    {
        static Stream Open(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        var good = Pain001SchemaProvider.Load(["good.xsd"], _ => Open(Pain001TestSchemas.AcceptingXsd));
        Assert.NotNull(good.Set);
        Assert.Null(good.Problem);
        Assert.Empty(XsdValidator.Validate($"<Document xmlns=\"{Pain001SchemaProvider.Namespace}\"><Anything/></Document>", good.Set!));

        var none = Pain001SchemaProvider.Load([], _ => throw new InvalidOperationException("there is nothing to open"));
        Assert.Null(none.Set);
        Assert.Null(none.Problem);

        var broken = Pain001SchemaProvider.Load(["broken.xsd"], _ => Open("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element"));
        Assert.Null(broken.Set);
        Assert.NotNull(broken.Problem);

        var hostile = Pain001SchemaProvider.Load(
            ["hostile.xsd"],
            _ => Open("<!DOCTYPE xs:schema [<!ENTITY e SYSTEM \"http://127.0.0.1:9/never\">]><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>"));
        Assert.Null(hostile.Set);
        Assert.NotNull(hostile.Problem);

        var other = Pain001SchemaProvider.Load(
            ["other.xsd"],
            _ => Open("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:other\"><xs:element name=\"X\" type=\"xs:string\"/></xs:schema>"));
        Assert.Null(other.Set);
        Assert.Contains("target namespace", other.Problem);
    }

    /// <summary>
    /// The container builds the service the way <c>Program.cs</c> registers it, with no schema source (the optional parameter is for the tests): it uses the
    /// official schemas of the build, so an org's batch is refused with its code and never validated against nothing.
    /// </summary>
    [Fact]
    public async Task WireTransferService_BuiltByTheContainer_UsesTheOfficialSchemasOfTheBuild_Test()
    {
        var orgId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(orgId));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"Pain001Di_{Guid.NewGuid()}"));
        services.AddScoped<IWireTransferService, WireTransferService>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<IWireTransferService>();

        Assert.IsType<WireTransferService>(service);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [], DebitIban));
        Assert.Equal("no_statements_provided", refused.Code);
    }

    // ---- the currency, the amounts and the lines --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The file is validated with the schema set of its own currency (EUR the SEPA one, CHF the Swiss one, which share a namespace and so can never be one
    /// set); a currency that has no wire file is 422 <c>wire_currency_not_supported</c>, from the service and from the provider.
    /// </summary>
    [Theory]
    [InlineData("EUR", OwnerIban)]
    [InlineData("CHF", SwissOwnerIban)]
    public async Task WireBatch_UsesTheSchemaOfItsCurrency_Test(string currency, string ownerIban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ownerIban);
        var statement = await AddStatementAsync(db, orgId, owner, 800.00m, currency);
        var requested = new List<string>();
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, c =>
        {
            requested.Add(c);
            return Pain001TestSchemas.Accepting();
        });

        var (batch, xml) = await service.CreateBatchAsync(orgId, [statement], DebitIban);

        Assert.Equal(currency, Assert.Single(requested));
        Assert.Equal(currency, batch.Currency);
        Assert.Equal(currency, (string?)XDocument.Parse(xml).Descendants(Pain + "InstdAmt").Single().Attribute("Ccy"));

        var unsupported = Assert.Throws<DomainRuleException>(() => Pain001SchemaProvider.GetSchemas("USD"));
        Assert.Equal(WireTransferErrors.CurrencyNotSupported, unsupported.Code);
    }

    /// <summary>
    /// Money rule: statements in a currency that has no wire file (a USD file is not a SEPA file) are 422 <c>wire_currency_not_supported</c> and nothing is
    /// saved; a letter case or a space in the stored code does not make a currency, it is the same one (EUR) and the file says it in capitals.
    /// </summary>
    [Fact]
    public async Task WireBatch_CurrencyWithoutAWireFile_IsRefused_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var dollars = await AddStatementAsync(db, orgId, mario, 500.00m, "USD");
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [dollars], DebitIban));

        Assert.Equal(WireTransferErrors.CurrencyNotSupported, refused.Code);
        await AssertNothingSavedAsync(db);

        var euros = await AddStatementAsync(db, orgId, mario, 500.00m, " eur ");
        var (batch, xml) = await service.CreateBatchAsync(orgId, [euros], DebitIban);

        Assert.Equal("EUR", batch.Currency);
        Assert.Equal("EUR", (string?)XDocument.Parse(xml).Descendants(Pain + "InstdAmt").Single().Attribute("Ccy"));
    }

    public static TheoryData<decimal> AmountsThatAreNotATransfer() => new() { 0m, -50m, 12.345m };

    /// <summary>
    /// Money rule: a transfer is a positive amount with at most two decimals. Zero, negative and a fraction of a cent are 422 <c>wire_amount_invalid</c> and
    /// nothing is saved (never a zero wire, never an amount rounded in silence); the amount of an owner is the sum of its statements, so two that cancel out
    /// are zero too.
    /// </summary>
    [Theory]
    [MemberData(nameof(AmountsThatAreNotATransfer))]
    public async Task WireBatch_AmountNotPositiveOrNotInCents_IsRefused_Test(decimal netPayable)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var statement = await AddStatementAsync(db, orgId, mario, netPayable);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [statement], DebitIban));

        Assert.Equal(WireTransferErrors.AmountInvalid, refused.Code);
        Assert.Empty(refused.MessageArgs);
        await AssertNothingSavedAsync(db);
    }

    [Fact]
    public async Task WireBatch_StatementsOfAnOwnerThatCancelOut_AreRefused_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var credit = await AddStatementAsync(db, orgId, mario, 100.00m);
        var debit = await AddStatementAsync(db, orgId, mario, -100.00m);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [credit, debit], DebitIban));

        Assert.Equal(WireTransferErrors.AmountInvalid, refused.Code);
        await AssertNothingSavedAsync(db);
    }

    /// <summary>
    /// Data rule: an empty list is never «all the statements»: it is 422 <c>no_statements_provided</c>, and no batch is made even though the org has published
    /// statements that a careless reading of the empty list would have taken.
    /// </summary>
    [Fact]
    public async Task WireBatch_EmptyStatementList_Is422AndNeverAll_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        await AddStatementAsync(db, orgId, mario, 1200.00m);
        await AddStatementAsync(db, orgId, mario, 300.00m);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [], DebitIban));

        Assert.Equal("no_statements_provided", refused.Code);
        await AssertNothingSavedAsync(db);
    }

    /// <summary>
    /// Privacy rule: an IBAN that does not pass the check digits (the debit account, an owner's) or an owner with none is refused with its code, and neither
    /// the IBAN nor the name of the owner is in the error, the arguments of the message, the exception or the log; nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("debit", "invalid_debit_account_iban")]
    [InlineData("owner", "owner_invalid_iban")]
    [InlineData("missing", "owner_missing_iban")]
    public async Task WireBatch_InvalidIban_IsRefusedWithoutTheIbanOrTheName_Test(string which, string code)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", which switch { "owner" => BadIban, "missing" => null, _ => OwnerIban });
        var statement = await AddStatementAsync(db, orgId, mario, 1200.00m);
        var logger = new CapturingLogger<WireTransferService>();
        var service = new WireTransferService(db, logger, _ => Pain001TestSchemas.Accepting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [statement], which == "debit" ? BadIban : DebitIban));

        Assert.Equal(code, refused.Code);
        Assert.Empty(refused.MessageArgs);
        AssertNoSecret(refused, logger);
        await AssertNothingSavedAsync(db);
    }

    /// <summary>
    /// Money rule: the amounts of the file are exact decimals and its sums are the sums of its lines. Three owners at 33.33, 33.33 and 33.34 and one with two
    /// statements of 0.10 and 0.20 make 100.30 to the cent in the control sum of the header and of the payment, four operations, one amount per owner with
    /// two decimals and the currency written, and four end-to-end ids that are all different: what a floating point sum would not give.
    /// </summary>
    [Fact]
    public async Task WireFile_AmountsAreExactDecimals_SumAndCountMatchTheRows_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherOwnerIban);
        var giulia = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", OwnerIban);
        var ids = new List<Guid>
        {
            await AddStatementAsync(db, orgId, mario, 0.10m),
            await AddStatementAsync(db, orgId, mario, 0.20m),
            await AddStatementAsync(db, orgId, luigi, 33.33m),
            await AddStatementAsync(db, orgId, giulia, 33.33m),
            await AddStatementAsync(db, orgId, await AddOwnerAsync(db, orgId, "Anna", "Neri", OwnerIban), 33.34m),
        };
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        var (batch, xml) = await service.CreateBatchAsync(orgId, ids, DebitIban);

        var document = XDocument.Parse(xml);
        var amounts = document.Descendants(Pain + "InstdAmt").Select(a => a.Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "0.30", "33.33", "33.33", "33.34" }, amounts);
        Assert.Equal(100.30m, amounts.Sum(a => decimal.Parse(a, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)));
        Assert.Equal(100.30m, batch.TotalAmount);
        Assert.Equal(4, batch.TransferCount);
        Assert.Equal(new[] { "100.30", "100.30" }, document.Descendants(Pain + "CtrlSum").Select(s => s.Value).ToArray());
        Assert.Equal(new[] { "4", "4" }, document.Descendants(Pain + "NbOfTxs").Select(s => s.Value).ToArray());
        Assert.All(document.Descendants(Pain + "InstdAmt"), a => Assert.Equal("EUR", (string?)a.Attribute("Ccy")));
        var endToEnd = document.Descendants(Pain + "EndToEndId").Select(e => e.Value).ToList();
        Assert.Equal(4, endToEnd.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(Pain001Checks.Violations(xml, "EUR", batch.TotalAmount, batch.TransferCount));
    }

    /// <summary>
    /// Money rule: every sum and count the checks know is caught when the file says something else than its lines (control sum or number of operations of the
    /// header and of the payment, a zero, negative or fractional amount, another or no currency, a repeated or missing end-to-end id), by the name of the
    /// element that is broken and never by a value.
    /// </summary>
    [Theory]
    [InlineData("header-control-sum", "CtrlSum")]
    [InlineData("payment-control-sum", "CtrlSum")]
    [InlineData("header-count", "NbOfTxs")]
    [InlineData("payment-count", "NbOfTxs")]
    [InlineData("zero-amount", "InstdAmt")]
    [InlineData("negative-amount", "InstdAmt")]
    [InlineData("three-decimals", "InstdAmt")]
    [InlineData("exponent-amount", "InstdAmt")]
    [InlineData("other-currency", "Ccy")]
    [InlineData("no-currency", "Ccy")]
    [InlineData("duplicate-end-to-end-id", "EndToEndId")]
    [InlineData("no-end-to-end-id", "EndToEndId")]
    [InlineData("no-payment", "CstmrCdtTrfInitn")]
    public async Task Pain001Checks_ReportABrokenRule_ByTheNameOfTheElement_Test(string mutation, string rule)
    {
        var (batch, xml) = await WriteTwoTransfersAsync();
        Assert.Empty(Pain001Checks.Violations(xml, batch.Currency, batch.TotalAmount, batch.TransferCount));

        var violations = Pain001Checks.Violations(Mutate(xml, mutation), batch.Currency, batch.TotalAmount, batch.TransferCount);

        Assert.Contains(rule, violations);
        Assert.All(violations, v => Assert.DoesNotContain(v, Secrets));
        Assert.Equal(violations.Count, violations.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The control sum and the count the batch says are checked too: a file consistent with itself but not with its batch is refused.</summary>
    [Fact]
    public async Task Pain001Checks_FileThatDoesNotMatchItsBatch_IsRefused_Test()
    {
        var (batch, xml) = await WriteTwoTransfersAsync();

        Assert.Equal(new[] { "CtrlSum" }, Pain001Checks.Violations(xml, batch.Currency, batch.TotalAmount + 0.01m, batch.TransferCount));
        Assert.Equal(new[] { "NbOfTxs" }, Pain001Checks.Violations(xml, batch.Currency, batch.TotalAmount, batch.TransferCount + 1));
        Assert.Equal(new[] { "Ccy" }, Pain001Checks.Violations(xml, "CHF", batch.TotalAmount, batch.TransferCount));
    }

    /// <summary>
    /// The validation reads no external entity and opens no connection: a file with a DTD, even one that points at an address, is a schema error and is never
    /// parsed (the checks refuse it too), so nothing outside the text is ever read.
    /// </summary>
    [Fact]
    public void WireFile_DtdAndExternalEntity_AreNeverRead_Test()
    {
        const string hostile =
            "<?xml version=\"1.0\"?><!DOCTYPE Document [<!ENTITY secret SYSTEM \"http://127.0.0.1:9/never\">]>" +
            "<Document xmlns=\"" + Pain001SchemaProvider.Namespace + "\">&secret;</Document>";

        Assert.NotEmpty(XsdValidator.Validate(hostile, Pain001TestSchemas.Accepting()));
        Assert.Throws<XmlException>(() => Pain001Checks.Violations(hostile, "EUR", 1.00m, 1));
    }

    // ---- who can reach the wires --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Permission and scope rule: the wire batches mix the properties of every owner of the org, so only the roles that reach every property hold
    /// <c>agency.wire.manage</c> (the owner and the administrator): the collaborator, the one role that can be limited to «Solo alcuni», never sees or validates
    /// a wire. A new role has to be added here on purpose.
    /// </summary>
    [Fact]
    public void WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test()
    {
        var holders = Enum.GetValues<OrgRole>()
            .Where(role => OrgRoleCatalog.AgencyPermissionsOf(role).Contains(AgencyPermissions.WireManage))
            .Order()
            .ToList();

        Assert.Equal(new[] { OrgRole.Owner, OrgRole.Admin }, holders);
        Assert.DoesNotContain(OrgRole.Collaborator, holders);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------------------------

    private static async Task<(WireBatch Batch, string Xml)> WriteTwoTransfersAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", OwnerIban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherOwnerIban);
        var ids = new List<Guid> { await AddStatementAsync(db, orgId, mario, 1200.00m), await AddStatementAsync(db, orgId, luigi, 1574.50m) };
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, _ => Pain001TestSchemas.Accepting());

        return await service.CreateBatchAsync(orgId, ids, DebitIban);
    }

    /// <summary>The file with one thing changed, as the name of the mutation says.</summary>
    private static string Mutate(string xml, string mutation)
    {
        var document = XDocument.Parse(xml);
        var header = document.Descendants(Pain + "GrpHdr").Single();
        var payment = document.Descendants(Pain + "PmtInf").Single();
        var amounts = document.Descendants(Pain + "InstdAmt").ToList();
        var endToEndIds = document.Descendants(Pain + "EndToEndId").ToList();

        switch (mutation)
        {
            case "header-control-sum": header.Element(Pain + "CtrlSum")!.Value = "1.00"; break;
            case "payment-control-sum": payment.Element(Pain + "CtrlSum")!.Value = "1.00"; break;
            case "header-count": header.Element(Pain + "NbOfTxs")!.Value = "9"; break;
            case "payment-count": payment.Element(Pain + "NbOfTxs")!.Value = "1"; break;
            case "zero-amount": amounts[0].Value = "0.00"; break;
            case "negative-amount": amounts[0].Value = "-5.00"; break;
            case "three-decimals": amounts[0].Value = "10.005"; break;
            case "exponent-amount": amounts[0].Value = "1E3"; break;
            case "other-currency": amounts[0].SetAttributeValue("Ccy", "USD"); break;
            case "no-currency": amounts[0].Attribute("Ccy")!.Remove(); break;
            case "duplicate-end-to-end-id": endToEndIds[1].Value = endToEndIds[0].Value; break;
            case "no-end-to-end-id": endToEndIds[0].Value = string.Empty; break;
            case "no-payment": payment.Remove(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return document.ToString();
    }

    private static void AssertNoSecret(DomainRuleException refused, CapturingLogger<WireTransferService> logger)
    {
        var shown = string.Join(
            " | ",
            refused.MessageArgs.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)).Append(refused.Message).Append(refused.ToString()));

        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, logger.AllOutput, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertNothingSavedAsync(AppDbContext db)
    {
        Assert.Empty(await db.WireBatches.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Set<WireBatchStatement>().IgnoreQueryFilters().ToListAsync());
    }

    private static AppDbContext NewDb(Guid orgId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Pain001_{Guid.NewGuid()}").Options;
        return new AppDbContext(options, new FixedTenantContext(orgId), new EphemeralDataProtectionProvider());
    }

    private static async Task<Guid> AddOwnerAsync(AppDbContext db, Guid orgId, string firstName, string lastName, string? iban)
    {
        var owner = new PropertyOwner
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{Guid.NewGuid():N}@example.com",
            Iban = iban,
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow,
        };
        db.PropertyOwners.Add(owner);
        await db.SaveChangesAsync();
        return owner.Id;
    }

    private static async Task<Guid> AddStatementAsync(AppDbContext db, Guid orgId, Guid ownerId, decimal netPayable, string currency = "EUR")
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Rome",
            CreatedAt = DateTime.UtcNow,
        };
        var statement = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PropertyId = property.Id,
            Month = 10,
            Year = 2026,
            Currency = currency,
            Status = StatementStatus.Published,
            TotalRevenue = netPayable,
            Commission = 0m,
            Expenses = 0m,
            WithholdingTax = 0m,
            NetPayable = netPayable,
            OwnershipPercent = 100.00m,
            PublishedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
        db.Properties.Add(property);
        db.OwnerStatements.Add(statement);
        await db.SaveChangesAsync();
        return statement.Id;
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
