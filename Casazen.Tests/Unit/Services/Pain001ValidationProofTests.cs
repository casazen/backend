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
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F05/US01/T01 (NF-25, AC-E06-F05-US01-01), carried on the payouts by E16/F01/US01/T01: the wire file is validated before the batch is saved. A file that does
/// not validate leaves no row behind, a file is never produced without its schema, the money of the file is exact (decimals, never a float) and matches its lines,
/// the errors carry no IBAN and no name, and the validation reads no external entity. The official schemas are not in the repository until an operator adds them
/// (docs/runbooks/wire-transfers.md): the tests that need to get past the validation give the service a schema of their own (<see cref="Pain001TestSchemas"/>).
/// The rules of the Swiss message itself, of the payouts and of the owners are in <see cref="Pain001V09Tests"/>.
/// </summary>
public class Pain001ValidationProofTests
{
    private static readonly XNamespace Swiss = Pain001SchemaProvider.SwissNamespace;
    private static readonly XNamespace Sepa = Pain001SchemaProvider.SepaNamespace;

    // ---- the proof of the brief ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Money rule: a file that fails the schema is refused (422 <c>wire_batch_xml_invalid</c>) and no batch row is written, not even the batch without its
    /// file the old code left behind. The payouts stay free, so a corrected retry makes exactly one batch. The error is the line and position of the
    /// schema errors, with no IBAN and no name, and the log carries counts only.
    /// </summary>
    [Fact]
    public async Task WireBatch_InvalidXml_NeverSaved_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var logger = new CapturingLogger<WireTransferService>();
        var service = NewService(db, logger, _ => Pain001TestSchemas.Rejecting());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, payouts, ExecutionDate));

        Assert.Equal(WireTransferErrors.XmlInvalid, refused.Code);
        Assert.Equal("WireBatchXmlInvalid", refused.MessageKey);
        Assert.Equal(2, refused.MessageArgs.Count);
        Assert.Matches(@"^\d+:\d+(, \d+:\d+)*$", (string)refused.MessageArgs[1]);
        await AssertNothingSavedAsync(db);
        AssertNoSecret(refused, logger);

        var (batch, _) = await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);

        Assert.Single(await db.WireBatches.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(2, (await db.WireBatchPayouts.IgnoreQueryFilters().ToListAsync()).Count);
        Assert.Equal(2774.50m, batch.TotalAmount);
    }

    /// <summary>
    /// With the official schema installed the file CasaZen writes validates against it and an altered one does not; without it nothing is produced (422
    /// <c>wire_batch_schema_unavailable</c>) and nothing is saved: never a file that was not validated, never a made-up success.
    /// </summary>
    [Theory]
    [InlineData("EUR", ItalianIban)]
    [InlineData("CHF", SwissIban)]
    public async Task WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test(string currency, string ownerIban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ownerIban);
        var payout = await AddPayoutAsync(db, orgId, owner, 800.00m, currency);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, null, Clock());

        if (!Pain001SchemaProvider.HasOfficialSchema(currency))
        {
            var unavailable = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [payout], ExecutionDate));
            Assert.Equal(WireTransferErrors.SchemaUnavailable, unavailable.Code);
            Assert.Equal("WireBatchSchemaUnavailable", unavailable.MessageKey);
            Assert.Equal(currency, Assert.Single(unavailable.MessageArgs));
            Assert.Null(Pain001SchemaProvider.LoadProblem(currency));
            await AssertNothingSavedAsync(db);
            return;
        }

        string xml;
        try
        {
            (_, xml) = await service.CreateBatchAsync(orgId, [payout], ExecutionDate);
        }
        catch (DomainRuleException refused)
        {
            // The writer has to change, not the schema: the failure says where (line and position, and the text of every error of the schema).
            Assert.Fail(await ExplainRefusedFileAsync(refused, currency, ownerIban));
            return;
        }

        var schemas = Pain001SchemaProvider.GetSchemas(currency);
        Assert.Empty(XsdValidator.Validate(xml, schemas));

        var ns = XNamespace.Get(Pain001SchemaProvider.NamespaceOf(currency));
        var altered = XDocument.Parse(xml);
        altered.Descendants(ns + "MsgId").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), schemas));
    }

    /// <summary>
    /// The embedded schemas either compile or the reason is known: a build never has files it cannot use and says nothing about it. The two currencies have
    /// each their own message (<c>pain.001.001.03</c> for EUR, <c>pain.001.001.09</c> for CHF).
    /// </summary>
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

        Assert.Equal(Pain001SchemaProvider.SepaNamespace, Pain001SchemaProvider.NamespaceOf(" eur "));
        Assert.Equal(Pain001SchemaProvider.SwissNamespace, Pain001SchemaProvider.NamespaceOf("CHF"));
        Assert.Equal("urn:iso:std:iso:20022:tech:xsd:pain.001.001.09", Pain001SchemaProvider.SwissNamespace);
        Assert.Equal(WireTransferErrors.CurrencyNotSupported, Assert.Throws<DomainRuleException>(() => Pain001SchemaProvider.NamespaceOf("USD")).Code);
    }

    /// <summary>
    /// The loader that compiles the official files the day an operator adds them, with stand-in files: a schema of the namespace the file is written in becomes a
    /// set that validates; no file is no set and no reason (the build simply has no schema); a file that does not compile, that has a DTD, that is another schema or
    /// the schema of the other message is no set and the reason is kept. A broken file is never taken for an installed schema.
    /// </summary>
    [Fact]
    public void Pain001SchemaProvider_Load_CompilesTheFiles_AndSaysWhyWhenItCannot_Test()
    {
        static Stream Open(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        var good = Pain001SchemaProvider.Load(["good.xsd"], _ => Open(Pain001TestSchemas.AcceptingXsdFor(Pain001SchemaProvider.SwissNamespace)), Pain001SchemaProvider.SwissNamespace);
        Assert.NotNull(good.Set);
        Assert.Null(good.Problem);
        Assert.Empty(XsdValidator.Validate($"<Document xmlns=\"{Pain001SchemaProvider.SwissNamespace}\"><Anything/></Document>", good.Set!));

        var none = Pain001SchemaProvider.Load([], _ => throw new InvalidOperationException("there is nothing to open"), Pain001SchemaProvider.SwissNamespace);
        Assert.Null(none.Set);
        Assert.Null(none.Problem);

        var broken = Pain001SchemaProvider.Load(
            ["broken.xsd"],
            _ => Open("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element"),
            Pain001SchemaProvider.SwissNamespace);
        Assert.Null(broken.Set);
        Assert.NotNull(broken.Problem);

        var hostile = Pain001SchemaProvider.Load(
            ["hostile.xsd"],
            _ => Open("<!DOCTYPE xs:schema [<!ENTITY e SYSTEM \"http://127.0.0.1:9/never\">]><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>"),
            Pain001SchemaProvider.SwissNamespace);
        Assert.Null(hostile.Set);
        Assert.NotNull(hostile.Problem);

        var other = Pain001SchemaProvider.Load(
            ["other.xsd"],
            _ => Open("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:other\"><xs:element name=\"X\" type=\"xs:string\"/></xs:schema>"),
            Pain001SchemaProvider.SwissNamespace);
        Assert.Null(other.Set);
        Assert.Contains("target namespace", other.Problem);

        // The schema of the SEPA message is not the schema of the Swiss one: a file written in the Swiss namespace would only draw a warning and pass.
        var sepaForSwiss = Pain001SchemaProvider.Load(
            ["sepa.xsd"],
            _ => Open(Pain001TestSchemas.AcceptingXsdFor(Pain001SchemaProvider.SepaNamespace)),
            Pain001SchemaProvider.SwissNamespace);
        Assert.Null(sepaForSwiss.Set);
        Assert.Contains(Pain001SchemaProvider.SwissNamespace, sepaForSwiss.Problem);
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
        services.AddSingleton<ITenantContext>(new WireTestTenant(orgId));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"Pain001Di_{Guid.NewGuid()}"));
        services.AddScoped<IWireTransferService, WireTransferService>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<IWireTransferService>();

        Assert.IsType<WireTransferService>(service);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [], ExecutionDate));
        Assert.Equal(WireTransferErrors.NoPayoutsProvided, refused.Code);
    }

    // ---- the currency, the amounts and the lines --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The file is validated with the schema set of its own currency (EUR the SEPA one, CHF the Swiss one, which have two namespaces and so are two sets); a currency
    /// that has no wire file is 422 <c>wire_currency_not_supported</c>, from the service and from the provider.
    /// </summary>
    [Theory]
    [InlineData("EUR", ItalianIban)]
    [InlineData("CHF", SwissIban)]
    public async Task WireBatch_UsesTheSchemaOfItsCurrency_Test(string currency, string ownerIban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ownerIban);
        var payout = await AddPayoutAsync(db, orgId, owner, 800.00m, currency);
        var requested = new List<string>();
        var service = NewService(db, null, c =>
        {
            requested.Add(c);
            return Pain001TestSchemas.Accepting();
        });

        var (batch, xml) = await service.CreateBatchAsync(orgId, [payout], ExecutionDate);

        Assert.Equal(currency, Assert.Single(requested));
        Assert.Equal(currency, batch.Currency);
        var ns = currency == "CHF" ? Swiss : Sepa;
        Assert.Equal(currency, (string?)XDocument.Parse(xml).Descendants(ns + "InstdAmt").Single().Attribute("Ccy"));
        Assert.Equal(currency == "CHF" ? WireMessageFormats.Pain001V09Ch03 : WireMessageFormats.Pain001V03Sepa, batch.MessageFormat);

        var unsupported = Assert.Throws<DomainRuleException>(() => Pain001SchemaProvider.GetSchemas("USD"));
        Assert.Equal(WireTransferErrors.CurrencyNotSupported, unsupported.Code);
    }

    /// <summary>
    /// Money rule: a letter case or a space in the stored currency code does not make another currency: a payout in <c>" eur "</c> is a EUR payout, and the file
    /// says it in capitals, in the SEPA message.
    /// </summary>
    [Fact]
    public async Task WireBatch_CurrencyOfThePayout_LetterCaseAndSpacesAreTheSameCurrency_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ItalianIban);
        var first = await AddPayoutAsync(db, orgId, mario, 500.00m, " eur ");
        var second = await AddPayoutAsync(db, orgId, mario, 250.00m, "EUR");

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, [first, second], ExecutionDate);

        Assert.Equal("EUR", batch.Currency);
        Assert.All(XDocument.Parse(xml).Descendants(Sepa + "InstdAmt"), a => Assert.Equal("EUR", (string?)a.Attribute("Ccy")));
        Assert.Equal(750.00m, batch.TotalAmount);
    }

    /// <summary>
    /// Data rule: an empty list is never «all the payouts»: it is 422 <c>no_payouts_provided</c>, and no batch is made even though the org has confirmed payouts that a
    /// careless reading of the empty list would have taken. A list is not a way to ask for everything either: more than a batch holds is 422
    /// <c>wire_batch_too_large</c>, and the same id twice is one transfer.
    /// </summary>
    [Fact]
    public async Task WireBatch_EmptyPayoutList_Is422AndNeverAll_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var service = NewService(db);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [], ExecutionDate));
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.CreateBatchAsync(orgId, Enumerable.Range(0, WireTransferErrors.MaxTransfers + 1).Select(_ => Guid.NewGuid()).ToList(), ExecutionDate));

        Assert.Equal(WireTransferErrors.NoPayoutsProvided, refused.Code);
        Assert.Equal(WireTransferErrors.BatchTooLarge, tooMany.Code);
        Assert.Equal(WireTransferErrors.MaxTransfers, Assert.Single(tooMany.MessageArgs));
        await AssertNothingSavedAsync(db);

        var (batch, _) = await service.CreateBatchAsync(orgId, [payouts[0], payouts[0]], ExecutionDate);
        Assert.Equal(1, batch.TransferCount);
        Assert.Equal(1200.00m, batch.TotalAmount);
    }

    /// <summary>
    /// Money rule: the amounts of the file are exact decimals and its sums are the sums of its lines. Payouts of 0.10 and 0.20 to one owner and of 33.33, 33.33 and
    /// 33.34 to three more make 100.30 to the cent in the control sum of the header and of the payment, five operations (a payout is a transfer of its own, never
    /// added to another one), one amount per payout with two decimals and the currency written, and five end-to-end ids that are all different: what a floating
    /// point sum would not give. In both currencies.
    /// </summary>
    [Theory]
    [InlineData("CHF")]
    [InlineData("EUR")]
    public async Task WireFile_AmountsAreExactDecimals_SumAndCountMatchTheRows_Test(string currency)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var ibans = currency == "CHF" ? new[] { SwissIban, OtherSwissIban } : [ItalianIban, OtherItalianIban];
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ibans[0]);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", ibans[1]);
        var giulia = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", ibans[0]);
        var anna = await AddOwnerAsync(db, orgId, "Anna", "Neri", ibans[1]);
        var ids = new List<Guid>
        {
            await AddPayoutAsync(db, orgId, mario, 0.10m, currency),
            await AddPayoutAsync(db, orgId, mario, 0.20m, currency),
            await AddPayoutAsync(db, orgId, luigi, 33.33m, currency),
            await AddPayoutAsync(db, orgId, giulia, 33.33m, currency),
            await AddPayoutAsync(db, orgId, anna, 33.34m, currency),
        };

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, ids, ExecutionDate);

        var ns = currency == "CHF" ? Swiss : Sepa;
        var document = XDocument.Parse(xml);
        var amounts = document.Descendants(ns + "InstdAmt").Select(a => a.Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "0.10", "0.20", "33.33", "33.33", "33.34" }, amounts);
        Assert.Equal(100.30m, amounts.Sum(a => decimal.Parse(a, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)));
        Assert.Equal(100.30m, batch.TotalAmount);
        Assert.Equal(5, batch.TransferCount);
        Assert.Equal(new[] { "100.30", "100.30" }, document.Descendants(ns + "CtrlSum").Select(s => s.Value).ToArray());
        Assert.Equal(new[] { "5", "5" }, document.Descendants(ns + "NbOfTxs").Select(s => s.Value).ToArray());
        Assert.All(document.Descendants(ns + "InstdAmt"), a => Assert.Equal(currency, (string?)a.Attribute("Ccy")));
        var endToEnd = document.Descendants(ns + "EndToEndId").Select(e => e.Value).ToList();
        Assert.Equal(5, endToEnd.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.Select(WireBatchPayout.EndToEndIdOf).Order(), endToEnd.Order());
        Assert.All(endToEnd, id => Assert.True(id.Length <= 35));
        Assert.Empty(Pain001Checks.Violations(xml, currency, batch.TotalAmount, batch.TransferCount));
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

    /// <summary>
    /// The control sum and the count the batch says are checked too: a file consistent with itself but not with its batch is refused. And the elements are read in
    /// the namespace of the message of the currency: a file of the other message has no initiation message at all.
    /// </summary>
    [Fact]
    public async Task Pain001Checks_FileThatDoesNotMatchItsBatch_IsRefused_Test()
    {
        var (batch, xml) = await WriteTwoTransfersAsync();

        Assert.Equal(new[] { "CtrlSum" }, Pain001Checks.Violations(xml, batch.Currency, batch.TotalAmount + 0.01m, batch.TransferCount));
        Assert.Equal(new[] { "NbOfTxs" }, Pain001Checks.Violations(xml, batch.Currency, batch.TotalAmount, batch.TransferCount + 1));
        Assert.Equal(new[] { "CstmrCdtTrfInitn" }, Pain001Checks.Violations(xml, "EUR", batch.TotalAmount, batch.TransferCount));
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
            "<Document xmlns=\"" + Pain001SchemaProvider.SwissNamespace + "\">&secret;</Document>";

        Assert.NotEmpty(XsdValidator.Validate(hostile, Pain001TestSchemas.Accepting()));
        Assert.Throws<XmlException>(() => Pain001Checks.Violations(hostile, "CHF", 1.00m, 1));
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

    /// <summary>
    /// What a person who has to fix the writer needs when the file CasaZen writes does not validate against the official schema of its currency: the refusal
    /// (its code and arguments: the count with the lines and positions of the schema errors, or the broken rules) and every error of the schema on the same
    /// file with its line, position and text. The file is written with a schema that accepts anything. Its data are the invented ones of these tests, so the
    /// text of an error may quote them without harm.
    /// </summary>
    private static async Task<string> ExplainRefusedFileAsync(DomainRuleException refused, string currency, string ownerIban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ownerIban);
        var payout = await AddPayoutAsync(db, orgId, owner, 800.00m, currency);
        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, [payout], ExecutionDate);
        var arguments = string.Join(" | ", refused.MessageArgs);
        var brokenRules = string.Join(", ", Pain001Checks.Violations(xml, currency, batch.TotalAmount, batch.TransferCount));

        var lines = new List<string>
        {
            $"The {currency} file does not validate against its official schema: {refused.Code} ({arguments}).",
            "Errors of the schema on the file (line:position text):",
        };
        lines.AddRange(XsdValidator.Validate(xml, Pain001SchemaProvider.GetSchemas(currency)).Select(e => $"  {e.Line}:{e.Position} {e.Message}"));
        lines.Add($"Rules the file breaks beyond the schema: {brokenRules}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A CHF batch of two transfers and its Swiss file.</summary>
    private static async Task<(WireBatch Batch, string Xml)> WriteTwoTransfersAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);

        return await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);
    }

    /// <summary>The file with one thing changed, as the name of the mutation says.</summary>
    private static string Mutate(string xml, string mutation)
    {
        var document = XDocument.Parse(xml);
        var header = document.Descendants(Swiss + "GrpHdr").Single();
        var payment = document.Descendants(Swiss + "PmtInf").Single();
        var amounts = document.Descendants(Swiss + "InstdAmt").ToList();
        var endToEndIds = document.Descendants(Swiss + "EndToEndId").ToList();

        switch (mutation)
        {
            case "header-control-sum": header.Element(Swiss + "CtrlSum")!.Value = "1.00"; break;
            case "payment-control-sum": payment.Element(Swiss + "CtrlSum")!.Value = "1.00"; break;
            case "header-count": header.Element(Swiss + "NbOfTxs")!.Value = "9"; break;
            case "payment-count": payment.Element(Swiss + "NbOfTxs")!.Value = "1"; break;
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
}
