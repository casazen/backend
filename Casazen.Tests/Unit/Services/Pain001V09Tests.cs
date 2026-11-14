using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E16/F01/US01/T01 (NF-25, NF-113, AC-NF113-3): the CHF wire file is the Swiss <c>pain.001.001.09.ch.03</c> message with structured addresses, built from the
/// <b>confirmed payouts</b> of the owners (the net after the withholding), validated against the official schema before anything is saved, encrypted at rest,
/// and its endpoints check the organization. The official schema is not in the repository until an operator adds it (docs/runbooks/wire-transfers.md): the proof
/// that needs it is skipped with a clear message while it is missing, and the other tests give the service a schema of their own (<see cref="Pain001TestSchemas"/>).
/// </summary>
public class Pain001V09Tests
{
    private static readonly XNamespace Swiss = Pain001SchemaProvider.SwissNamespace;
    private static readonly XNamespace Sepa = Pain001SchemaProvider.SepaNamespace;

    // ---- the proofs of the brief ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Money and data rule (AC-NF113-3): a CHF batch built from confirmed payouts is a <c>pain.001.001.09.ch.03</c> file that validates against the OFFICIAL schema of
    /// the Swiss Payment Standards, with the schema of the build (no stand-in), and an altered file does not validate. Skipped with its reason while the official
    /// file is not in <c>Payments/Schemas/Swiss</c>: nothing is claimed valid that was not checked (the service answers 422 <c>wire_batch_schema_unavailable</c>
    /// then, never a made-up success).
    /// </summary>
    [OfficialPain001SchemaFact(WireTransferErrors.SwissCurrency)]
    public async Task Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, null, Clock());

        string xml;
        try
        {
            (_, xml) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);
        }
        catch (DomainRuleException refused)
        {
            // The writer has to change, not the schema: the failure says where (line and position, and the text of every error of the schema).
            Assert.Fail(await ExplainRefusedFileAsync(refused));
            return;
        }

        var schemas = Pain001SchemaProvider.GetSchemas(WireTransferErrors.SwissCurrency);
        Assert.Empty(XsdValidator.Validate(xml, schemas));

        var altered = XDocument.Parse(xml);
        altered.Descendants(Swiss + "MsgId").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), schemas));
    }

    /// <summary>
    /// Money rule: the amount of a transfer is the NET of the payout (the gross minus the withholding), never the gross and never the old
    /// <c>OwnerStatement.NetPayable</c>. The batch total, every <c>InstdAmt</c>, the control sums and the amount kept for each payout are the nets, to the cent,
    /// in CHF and in EUR (where the withholding of an Italian owner is).
    /// </summary>
    [Theory]
    [InlineData("CHF", "1200.00", "0.00")]
    [InlineData("EUR", "790.00", "210.00")]
    public async Task WireBatch_AmountIsPayoutNet_Test(string currency, string netText, string withholdingText)
    {
        var net = decimal.Parse(netText, CultureInfo.InvariantCulture);
        var withholding = decimal.Parse(withholdingText, CultureInfo.InvariantCulture);
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var iban = currency == "CHF" ? SwissIban : ItalianIban;
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", iban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", iban == SwissIban ? OtherSwissIban : OtherItalianIban);
        var first = await AddPayoutAsync(db, orgId, mario, net, currency, withholding);
        var second = await AddPayoutAsync(db, orgId, luigi, 33.33m, currency);

        // The old source of the amount, the net of the statement of the owner, is something else and must have no part in the file.
        db.OwnerStatements.Add(new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = mario,
            PropertyId = Guid.NewGuid(),
            Month = 10,
            Year = 2026,
            Currency = currency,
            Status = StatementStatus.Published,
            TotalRevenue = 5000.00m,
            NetPayable = 5000.00m,
            OwnershipPercent = 100.00m,
            PublishedAt = Now.UtcDateTime,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, [first, second], ExecutionDate);

        var ns = currency == "CHF" ? Swiss : Sepa;
        var document = XDocument.Parse(xml);
        var amounts = document.Descendants(ns + "InstdAmt").Select(a => a.Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "33.33", net.ToString("F2", CultureInfo.InvariantCulture) }.Order(StringComparer.Ordinal), amounts);
        Assert.DoesNotContain((net + withholding).ToString("F2", CultureInfo.InvariantCulture), amounts.Where(a => withholding > 0m));
        Assert.DoesNotContain("5000.00", xml);
        Assert.Equal(net + 33.33m, batch.TotalAmount);
        Assert.Equal(2, batch.TransferCount);
        Assert.All(document.Descendants(ns + "CtrlSum"), s => Assert.Equal((net + 33.33m).ToString("F2", CultureInfo.InvariantCulture), s.Value));
        Assert.All(document.Descendants(ns + "InstdAmt"), a => Assert.Equal(currency, (string?)a.Attribute("Ccy")));

        var rows = await db.WireBatchPayouts.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(new[] { 33.33m, net }.Order(), rows.Select(r => r.Amount).Order());
        Assert.Equal(new[] { first, second }.Order(), rows.Select(r => r.OwnerPayoutId).Order());
    }

    /// <summary>
    /// Privacy rule: the file holds the IBAN, the name and the address of every owner, so it is stored encrypted. The two columns of the batch that hold them (the file
    /// and the debit account of the agency) are declared in <see cref="EncryptedColumns"/> and carry the converter in the model of a context with Data Protection, which is
    /// what EF writes to the table: applied to the real file of a real batch it gives a Data Protection payload without any IBAN, name or address, and the same converter
    /// gives the file back, to the byte; read through EF the service returns the file. (The EF in-memory database is not a table and does not show what a real column holds,
    /// so the converter of the model is the proof here: the one mechanism every other encrypted column relies on, see <c>EncryptedColumnsTests</c>.)
    /// </summary>
    [Fact]
    public async Task WireBatch_XmlEncryptedAtRest_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var service = NewService(db);

        var (batch, xml) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);

        Assert.Contains(SwissIban, xml);
        Assert.True(EncryptedColumns.IsConfigured(db));
        var entity = db.Model.FindEntityType(typeof(WireBatch))!;
        foreach (var (property, clear) in new[] { (nameof(WireBatch.XmlContent), xml), (nameof(WireBatch.DebitAccount), DebitIban) })
        {
            Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(WireBatch) && c.Property == property && c.Purpose == EncryptedColumns.WireBatchFilePurpose);

            // What EF writes in the column: the payload of Data Protection, with none of the secrets of the file in it.
            var converter = Assert.IsType<EncryptedStringConverter>(entity.FindProperty(property)!.GetValueConverter());
            var written = Assert.IsType<string>(converter.ConvertToProvider(clear));
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, written, StringComparison.Ordinal);
            foreach (var secret in new[] { SwissIban, OtherSwissIban, DebitIban, "Mario", "Rossi", "Luigi", "Bianchi", "Via Cantonale", "Lugano", "Quokka", "<Document" })
                Assert.DoesNotContain(secret, written, StringComparison.Ordinal);

            // And what it gives back is the clear value, to the byte.
            Assert.Equal(clear, converter.ConvertFromProvider(written));
        }

        // The model of a context without Data Protection holds no converter: the encryption is the model's, not a habit of this test.
        await using var plain = new AppDbContext(NewOptions());
        Assert.Null(plain.Model.FindEntityType(typeof(WireBatch))!.FindProperty(nameof(WireBatch.XmlContent))!.GetValueConverter());

        // Read through EF the service gives the file back, and the file of the endpoint is the one that was written.
        var (_, file) = await service.GetBatchFileAsync(orgId, batch.Id);
        Assert.Equal(xml, file);
        Assert.Equal(xml, (await db.WireBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id)).XmlContent);
    }

    /// <summary>
    /// Permission rule: the confirmations check the organization explicitly, not only through the global filter. With the filter OFF (a context that cannot tell
    /// the orgs apart) a batch of another org is still a 404 for the download, the confirm-download, the confirm-paid and the list, and its state does not move;
    /// the org of the batch can walk it through its states.
    /// </summary>
    [Fact]
    public async Task WireBatch_ConfirmOtherOrganization404_Test()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await using var db = NewDb(orgId, filterEnabled: false);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var service = NewService(db);
        var (batch, _) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmDownloadAsync(otherOrgId, batch.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmPaidAsync(otherOrgId, batch.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetBatchFileAsync(otherOrgId, batch.Id));
        Assert.Empty(await service.ListBatchesAsync(otherOrgId));

        var untouched = await db.WireBatches.AsNoTracking().SingleAsync();
        Assert.Equal(WireBatchStatus.Generated, untouched.Status);
        Assert.Null(untouched.DownloadedAt);
        Assert.Null(untouched.PaidAt);

        await service.ConfirmDownloadAsync(orgId, batch.Id);
        await service.ConfirmPaidAsync(orgId, batch.Id);
        var paid = await db.WireBatches.AsNoTracking().SingleAsync();
        Assert.Equal(WireBatchStatus.Paid, paid.Status);
        Assert.Equal(Now.UtcDateTime, paid.PaidAt);
    }

    // ---- the file --------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The CHF file is the Swiss message and nothing else: the <c>pain.001.001.09</c> namespace and its schema location, one payment per execution date with its
    /// count, sum, date (<c>ReqdExctnDt/Dt</c>), debtor and IBAN, one transfer per payout with the unique end-to-end id of the payout, the amount, the creditor with a
    /// <b>structured</b> address (and no address line), the creditor IBAN and the remittance «Rendiconto 10/2026». The children of every element follow the ISO 20022
    /// sequence, which a schema checks and this test keeps checked while the official file is missing.
    /// </summary>
    [Fact]
    public async Task Pain001V09_ChfFile_IsTheSwissMessageWithStructuredAddresses_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);

        var document = XDocument.Parse(xml);
        Assert.Equal(Swiss + "Document", document.Root!.Name);
        Assert.Equal(
            Pain001SchemaProvider.SwissNamespace + " pain.001.001.09.ch.03.xsd",
            (string?)document.Root.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "schemaLocation"));
        var initiation = Assert.Single(document.Root.Elements());
        Assert.Equal("CstmrCdtTrfInitn", initiation.Name.LocalName);
        Assert.Equal(new[] { "GrpHdr", "PmtInf" }, Names(initiation));

        var header = initiation.Element(Swiss + "GrpHdr")!;
        Assert.Equal(new[] { "MsgId", "CreDtTm", "NbOfTxs", "CtrlSum", "InitgPty" }, Names(header));
        Assert.Equal("CZ" + batch.Id.ToString("N"), header.Element(Swiss + "MsgId")!.Value);
        Assert.Equal("2026-11-14T11:00:00", header.Element(Swiss + "CreDtTm")!.Value);
        Assert.Equal("2", header.Element(Swiss + "NbOfTxs")!.Value);
        Assert.Equal("2774.50", header.Element(Swiss + "CtrlSum")!.Value);
        Assert.Equal(new[] { "Nm" }, Names(header.Element(Swiss + "InitgPty")!));

        var payment = initiation.Element(Swiss + "PmtInf")!;
        Assert.Equal(
            new[] { "PmtInfId", "PmtMtd", "BtchBookg", "NbOfTxs", "CtrlSum", "ReqdExctnDt", "Dbtr", "DbtrAcct", "DbtrAgt", "CdtTrfTxInf", "CdtTrfTxInf" },
            Names(payment));
        Assert.True(payment.Element(Swiss + "PmtInfId")!.Value.Length <= 35);
        Assert.Equal("TRF", payment.Element(Swiss + "PmtMtd")!.Value);
        Assert.Equal("2", payment.Element(Swiss + "NbOfTxs")!.Value);
        Assert.Equal("2774.50", payment.Element(Swiss + "CtrlSum")!.Value);
        Assert.Equal("2026-11-16", payment.Element(Swiss + "ReqdExctnDt")!.Element(Swiss + "Dt")!.Value);

        var debtor = payment.Element(Swiss + "Dbtr")!;
        Assert.Equal(new[] { "Nm", "PstlAdr" }, Names(debtor));
        Assert.Equal("Quokka Immobiliare SA", debtor.Element(Swiss + "Nm")!.Value);
        Assert.Equal(new[] { "StrtNm", "BldgNb", "PstCd", "TwnNm", "Ctry" }, Names(debtor.Element(Swiss + "PstlAdr")!));
        Assert.Equal(DebitIban, payment.Element(Swiss + "DbtrAcct")!.Element(Swiss + "Id")!.Element(Swiss + "IBAN")!.Value);
        Assert.Equal("NOTPROVIDED", payment.Element(Swiss + "DbtrAgt")!.Element(Swiss + "FinInstnId")!.Element(Swiss + "Othr")!.Element(Swiss + "Id")!.Value);

        var transfers = payment.Elements(Swiss + "CdtTrfTxInf").ToList();
        Assert.Equal(payouts.Select(WireBatchPayout.EndToEndIdOf).Order(), transfers.Select(t => t.Element(Swiss + "PmtId")!.Element(Swiss + "EndToEndId")!.Value).Order());
        foreach (var transfer in transfers)
        {
            Assert.Equal(new[] { "PmtId", "Amt", "Cdtr", "CdtrAcct", "RmtInf" }, Names(transfer));
            var creditor = transfer.Element(Swiss + "Cdtr")!;
            Assert.Equal(new[] { "Nm", "PstlAdr" }, Names(creditor));
            var address = creditor.Element(Swiss + "PstlAdr")!;
            Assert.Equal(new[] { "StrtNm", "BldgNb", "PstCd", "TwnNm", "Ctry" }, Names(address));
            Assert.Equal(new[] { "Via Cantonale", "12", "6900", "Lugano", "CH" }, address.Elements().Select(e => e.Value).ToArray());
            Assert.Equal("Rendiconto 10/2026", transfer.Element(Swiss + "RmtInf")!.Element(Swiss + "Ustrd")!.Value);
        }

        Assert.DoesNotContain("AdrLine", xml);
        Assert.Equal(WireMessageFormats.Pain001V09Ch03, batch.MessageFormat);
        Assert.Equal(ExecutionDate, batch.ExecutionDate);
        Assert.Empty(Pain001Checks.Violations(xml, "CHF", batch.TotalAmount, batch.TransferCount));
        Assert.Empty(XsdValidator.Validate(xml, Pain001TestSchemas.Accepting()));
    }

    /// <summary>
    /// One payment per requested execution date: transfers that ask for two dates are two payments in date order, each with its own id, count and control sum, and
    /// the header adds them up. (The request of the API has one date; the writer keeps the rule of the message.)
    /// </summary>
    [Fact]
    public void Pain001V09Writer_OnePaymentPerExecutionDate_Test()
    {
        var creditor = new Pain001Party("Mario Rossi", "Via Cantonale", "12", "6900", "Lugano", "ch");
        var debtor = new Pain001Party("Quokka Immobiliare SA", "Via Nassa", null, "6900", "Lugano", "CH");
        var message = new Pain001Message(
            "CZ" + Guid.NewGuid().ToString("N"),
            new DateTime(2026, 11, 14, 11, 0, 0),
            "CHF",
            debtor,
            DebitIban,
            [
                new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 10.10m, creditor, SwissIban, "Rendiconto 10/2026", new DateOnly(2026, 11, 20)),
                new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 20.20m, creditor, SwissIban, "Rendiconto 10/2026", new DateOnly(2026, 11, 16)),
                new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 5.05m, creditor, SwissIban, "Rendiconto 10/2026", new DateOnly(2026, 11, 20)),
            ]);

        var xml = Pain001V09Writer.Write(message);

        var payments = XDocument.Parse(xml).Descendants(Swiss + "PmtInf").ToList();
        Assert.Equal(2, payments.Count);
        Assert.Equal(new[] { "2026-11-16", "2026-11-20" }, payments.Select(p => p.Element(Swiss + "ReqdExctnDt")!.Element(Swiss + "Dt")!.Value).ToArray());
        Assert.Equal(new[] { "1", "2" }, payments.Select(p => p.Element(Swiss + "NbOfTxs")!.Value).ToArray());
        Assert.Equal(new[] { "20.20", "15.15" }, payments.Select(p => p.Element(Swiss + "CtrlSum")!.Value).ToArray());
        Assert.Equal(2, payments.Select(p => p.Element(Swiss + "PmtInfId")!.Value).Distinct().Count());
        Assert.All(payments, p => Assert.True(p.Element(Swiss + "PmtInfId")!.Value.Length <= 35));
        Assert.Equal("3", XDocument.Parse(xml).Descendants(Swiss + "GrpHdr").Single().Element(Swiss + "NbOfTxs")!.Value);
        Assert.Equal("35.35", XDocument.Parse(xml).Descendants(Swiss + "GrpHdr").Single().Element(Swiss + "CtrlSum")!.Value);
        Assert.Equal("CH", XDocument.Parse(xml).Descendants(Swiss + "Cdtr").First().Descendants(Swiss + "Ctry").Single().Value);
        Assert.Empty(Pain001Checks.Violations(xml, "CHF", 35.35m, 3));
    }

    /// <summary>
    /// The texts of the file are what the Swiss messages accept: letters of Latin-1, digits and <c>/ - ? : ( ) . , ' + &amp;</c> stay, other accented letters
    /// lose their accent, signs and control characters become spaces (collapsed), what has no equivalent is a point, and a text is cut to the length of its element
    /// without a trailing space. An address that is empty after that is not complete.
    /// </summary>
    [Fact]
    public void Pain001Text_Clean_KeepsTheSwissCharactersAndCutsTheLengths_Test()
    {
        Assert.Equal("Müller & Söhne AG", Pain001Text.Clean("  Müller &  Söhne AG ", 70));
        Assert.Equal("Lukasz Zólc", Pain001Text.Clean("Łukasz Żółć", 70));
        Assert.Equal("b x /b", Pain001Text.Clean("<b>x</b>", 70));
        Assert.Equal("riga uno riga due", Pain001Text.Clean("riga uno\r\nriga\tdue", 70));
        Assert.Equal("..", Pain001Text.Clean("日本", 70));
        Assert.Equal("A.", Pain001Text.Clean("A😀", 70));
        Assert.Equal(string.Empty, Pain001Text.Clean("   ", 70));
        Assert.Equal(string.Empty, Pain001Text.Clean(null, 70));

        var long80 = new string('a', 34) + " " + new string('b', 45);
        var cut = Pain001Text.Clean(long80, 35);
        Assert.Equal(34, cut.Length);
        Assert.Equal(70, Pain001Text.Clean(new string('x', 200), Pain001V09Writer.Max70).Length);

        Assert.Equal("CH", Pain001Text.Country(" ch "));
        Assert.Null(Pain001Text.Country("CHE"));
        Assert.Null(Pain001Text.Country("1H"));
        Assert.Null(Pain001Text.Country(null));

        var complete = new Pain001Party("Mario Rossi", "Via Cantonale", null, "6900", "Lugano", "CH");
        Assert.True(Pain001V09Writer.IsComplete(complete));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Street = "  " }));
        Assert.False(Pain001V09Writer.IsComplete(complete with { PostalCode = null }));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Town = string.Empty }));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Country = "Svizzera" }));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Name = "\t" }));
    }

    // ---- the owners and the account ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Data rule: an owner without a complete structured address or without an IBAN is refused for the batch with 422 <c>wire_owner_address_missing</c> or
    /// <c>wire_owner_iban_missing</c>, which lists the ids of the owners (never a name or an IBAN: not in the answer, the exception or the log), and nothing is saved.
    /// The owners that are fine are not listed, and once the data is complete the same request makes the batch.
    /// </summary>
    [Theory]
    [InlineData("address", WireTransferErrors.OwnerAddressMissing)]
    [InlineData("iban", WireTransferErrors.OwnerIbanMissing)]
    public async Task WireBatch_OwnerWithoutAddressOrIban_IsRefusedWithTheOwnerIds_Test(string missing, string code)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var fine = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", SwissIban);
        var withoutAddress = await AddOwnerAsync(db, orgId, "Mario", "Rossi", SwissIban, withAddress: missing != "address");
        var withoutIban = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", missing == "iban" ? null : OtherSwissIban);
        var payouts = new List<Guid>
        {
            await AddPayoutAsync(db, orgId, fine, 100.00m),
            await AddPayoutAsync(db, orgId, withoutAddress, 200.00m),
            await AddPayoutAsync(db, orgId, withoutIban, 300.00m),
        };
        var logger = new CapturingLogger<WireTransferService>();
        var service = NewService(db, logger);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, payouts, ExecutionDate));

        Assert.Equal(code, refused.Code);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["ownerIds"]);
        var expected = missing == "address" ? new[] { withoutAddress } : new[] { withoutIban };
        Assert.Equal(expected.Order(), listed.Order());
        Assert.DoesNotContain(fine, listed);
        Assert.Equal(1, (int)Assert.Single(refused.MessageArgs));
        AssertNoSecret(refused, logger);
        await AssertNothingSavedAsync(db);

        // The data is completed: the same request makes the batch.
        var owner = await db.PropertyOwners.SingleAsync(o => o.Id == (missing == "address" ? withoutAddress : withoutIban));
        if (missing == "address")
        {
            (owner.BillingStreet, owner.BillingCivic, owner.BillingPostalCode, owner.BillingCity, owner.BillingCountry) = ("Via Cantonale", "3", "6900", "Lugano", "CH");
        }
        else
        {
            owner.Iban = OtherSwissIban;
        }

        await db.SaveChangesAsync();
        var (batch, _) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);
        Assert.Equal(3, batch.TransferCount);
    }

    /// <summary>
    /// Data rule (NF-113): a QR-IBAN is never the creditor account of a normal transfer: 422 <c>wire_owner_qr_iban</c> with the owner id and nothing saved, and the
    /// ordinary IBAN of the same owner is accepted. An IBAN that fails the check digits is 422 <c>owner_invalid_iban</c>; an Italian IBAN in a CHF batch is
    /// 422 <c>wire_owner_iban_not_swiss</c> (not a domestic payment), while a Liechtenstein IBAN is a domestic one, and an Italian IBAN is fine in a EUR batch.
    /// </summary>
    [Theory]
    [InlineData("CHF", QrIban, WireTransferErrors.OwnerQrIban)]
    [InlineData("CHF", BadIban, WireTransferErrors.OwnerIbanInvalid)]
    [InlineData("CHF", ItalianIban, WireTransferErrors.OwnerIbanNotSwiss)]
    [InlineData("CHF", LiechtensteinIban, null)]
    [InlineData("EUR", ItalianIban, null)]
    public async Task WireBatch_CreditorIban_IsACreditorAccountOfTheFile_OrRefused_Test(string currency, string iban, string? code)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", iban);
        var payout = await AddPayoutAsync(db, orgId, owner, 500.00m, currency);
        var logger = new CapturingLogger<WireTransferService>();
        var service = NewService(db, logger);

        if (code is null)
        {
            var (batch, xml) = await service.CreateBatchAsync(orgId, [payout], ExecutionDate);
            Assert.Equal(currency, batch.Currency);
            Assert.Contains(iban, xml);
            return;
        }

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [payout], ExecutionDate));
        Assert.Equal(code, refused.Code);
        Assert.Equal(owner, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["ownerIds"])));
        AssertNoSecret(refused, logger);
        await AssertNothingSavedAsync(db);

        // The owner gives its ordinary IBAN: the batch is made.
        (await db.PropertyOwners.SingleAsync()).Iban = SwissIban;
        await db.SaveChangesAsync();
        Assert.Equal(1, (await service.CreateBatchAsync(orgId, [payout], ExecutionDate)).Batch.TransferCount);
    }

    /// <summary>
    /// The debtor and the account to debit are the issuer profile of the org: no profile, no name, no street, no IBAN or an IBAN that fails the check digits is a
    /// refusal with its code (<c>wire_debtor_incomplete</c>, <c>wire_debit_account_missing</c>, <c>invalid_debit_account_iban</c>), nothing is saved and neither the
    /// IBAN nor the name is in the error or the log.
    /// </summary>
    [Theory]
    [InlineData("no-profile", WireTransferErrors.DebtorIncomplete)]
    [InlineData("no-name", WireTransferErrors.DebtorIncomplete)]
    [InlineData("no-street", WireTransferErrors.DebtorIncomplete)]
    [InlineData("no-iban", WireTransferErrors.DebitAccountMissing)]
    [InlineData("bad-iban", WireTransferErrors.DebitAccountInvalid)]
    public async Task WireBatch_DebtorIsTheIssuerProfile_OrTheBatchIsRefused_Test(string scenario, string code)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        switch (scenario)
        {
            case "no-name": await AddIssuerAsync(db, orgId, legalName: " "); break;
            case "no-street": await AddIssuerAsync(db, orgId, street: null); break;
            case "no-iban": await AddIssuerAsync(db, orgId, iban: null); break;
            case "bad-iban": await AddIssuerAsync(db, orgId, iban: BadIban); break;
            case "no-profile": break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var logger = new CapturingLogger<WireTransferService>();
        var service = NewService(db, logger);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, payouts, ExecutionDate));

        Assert.Equal(code, refused.Code);
        Assert.Empty(refused.MessageArgs);
        AssertNoSecret(refused, logger);
        await AssertNothingSavedAsync(db);
    }

    // ---- the payouts --------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Money rule: a payout is paid by one batch only. The same payouts sent twice make one batch the first time and are 422 <c>payouts_already_in_batch</c> the
    /// second time; a request that mixes a paid payout with a free one is refused too and leaves the free one free. The batch and one row per payout are saved once,
    /// never two payable files for the same money.
    /// </summary>
    [Fact]
    public async Task WireBatch_PayoutAlreadyInABatch_IsRefused_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", SwissIban);
        var first = await AddPayoutAsync(db, orgId, mario, 1200.00m);
        var second = await AddPayoutAsync(db, orgId, mario, 300.00m);
        var later = await AddPayoutAsync(db, orgId, mario, 50.00m);
        var service = NewService(db);

        var (batch, _) = await service.CreateBatchAsync(orgId, [first, second], ExecutionDate);
        var again = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [first, second], ExecutionDate));
        var mixed = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, [second, later], ExecutionDate));

        Assert.Equal(WireTransferErrors.PayoutsAlreadyInBatch, again.Code);
        Assert.Equal(WireTransferErrors.PayoutsAlreadyInBatch, mixed.Code);
        Assert.Equal("PayoutsAlreadyInBatch", again.MessageKey);
        Assert.Equal(batch.Id, Assert.Single(await db.WireBatches.IgnoreQueryFilters().ToListAsync()).Id);
        var rows = await db.WireBatchPayouts.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(batch.Id, row.WireBatchId));
        Assert.DoesNotContain(later, rows.Select(row => row.OwnerPayoutId));

        // The payout that was left free can still be paid, in a batch of its own.
        var (next, _) = await service.CreateBatchAsync(orgId, [later, later], ExecutionDate);
        Assert.Equal(1, next.TransferCount);
        Assert.Equal(50.00m, next.TotalAmount);
    }

    /// <summary>
    /// Data and money rules about which payouts can be paid: one of another org (or that does not exist) is 422 <c>payouts_not_found_or_not_owned</c>, a reversed
    /// one <c>wire_payouts_not_confirmed</c>, one of a version never published to the owner <c>wire_payouts_not_published</c>, payouts in two currencies
    /// <c>mixed_currencies_not_allowed</c>, a currency with no wire file <c>wire_currency_not_supported</c>. Each leaves nothing behind.
    /// </summary>
    [Theory]
    [InlineData("other-org", WireTransferErrors.PayoutsNotFound)]
    [InlineData("unknown", WireTransferErrors.PayoutsNotFound)]
    [InlineData("reversed", WireTransferErrors.PayoutsNotConfirmed)]
    [InlineData("not-published", WireTransferErrors.PayoutsNotPublished)]
    [InlineData("mixed-currencies", WireTransferErrors.MixedCurrencies)]
    [InlineData("usd", WireTransferErrors.CurrencyNotSupported)]
    public async Task WireBatch_PayoutsThatCannotBePaid_AreRefused_Test(string scenario, string code)
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", SwissIban);
        var good = await AddPayoutAsync(db, orgId, mario, 100.00m);
        var bad = scenario switch
        {
            "other-org" => await AddPayoutAsync(db, otherOrgId, await AddOwnerAsync(db, otherOrgId, "Anna", "Neri", SwissIban), 100.00m),
            "unknown" => Guid.NewGuid(),
            "reversed" => await AddPayoutAsync(db, orgId, mario, 100.00m, status: OwnerPayoutStatus.Reversed),
            "not-published" => await AddPayoutAsync(db, orgId, mario, 100.00m, published: false),
            "mixed-currencies" => await AddPayoutAsync(db, orgId, mario, 100.00m, "EUR"),
            "usd" => await AddPayoutAsync(db, orgId, mario, 100.00m, "USD"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
        var service = NewService(db);

        var ids = scenario == "usd" ? new List<Guid> { bad } : [good, bad];
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, ids, ExecutionDate));

        Assert.Equal(code, refused.Code);
        await AssertNothingSavedAsync(db);
    }

    /// <summary>Money rule: the amount of a transfer is above zero and has at most two decimals (the net of a payout is never zero, negative or a fraction of a cent).</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    [InlineData("12.345")]
    public async Task WireBatch_NetAmountNotPositiveOrNotInCents_IsRefused_Test(string netText)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", SwissIban);
        var payout = await AddPayoutAsync(db, orgId, mario, decimal.Parse(netText, CultureInfo.InvariantCulture));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => NewService(db).CreateBatchAsync(orgId, [payout], ExecutionDate));

        Assert.Equal(WireTransferErrors.AmountInvalid, refused.Code);
        Assert.Empty(refused.MessageArgs);
        await AssertNothingSavedAsync(db);
    }

    /// <summary>
    /// The bank is asked for today or a later day, within a year (Europe/Rome): a day before today, more than a year ahead and no date at all are 422
    /// <c>wire_execution_date_invalid</c> and save nothing; today and the last day of the year are accepted, and the date is the one of the file and of the batch.
    /// </summary>
    [Theory]
    [InlineData("2026-11-13", false)]
    [InlineData("2026-11-14", true)]
    [InlineData("2027-11-14", true)]
    [InlineData("2027-11-15", false)]
    [InlineData("0001-01-01", false)]
    public async Task WireBatch_ExecutionDate_IsTodayOrLaterWithinAYear_Test(string dateText, bool accepted)
    {
        var date = DateOnly.ParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var service = NewService(db);

        if (!accepted)
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, payouts, date));
            Assert.Equal(WireTransferErrors.ExecutionDateInvalid, refused.Code);
            await AssertNothingSavedAsync(db);
            return;
        }

        var (batch, xml) = await service.CreateBatchAsync(orgId, payouts, date);
        Assert.Equal(date, batch.ExecutionDate);
        Assert.Equal(dateText, XDocument.Parse(xml).Descendants(Swiss + "ReqdExctnDt").Single().Element(Swiss + "Dt")!.Value);
    }

    // ---- the request and the list ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Data rule: the request of before the payouts (statement ids, a typed debit account) is still read but refused with 422
    /// <c>wire_batch_from_statement_retired</c>, whatever else it says, and the service is never called: no batch is made from a statement and a debit account that
    /// was typed is never ignored in silence. A request with payouts and a date goes to the service as it is, and no date is the date that never is.
    /// </summary>
    [Fact]
    public async Task WireBatch_StatementBasedRequest_IsRetiredAndRefused_Test()
    {
        var orgId = Guid.NewGuid();
        var service = new Mock<IWireTransferService>(MockBehavior.Strict);
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var controller = new WireTransferController(service.Object, resolver.Object, NullLogger<WireTransferController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        foreach (var retired in new[]
                 {
                     new CreateBatchRequest { StatementIds = [Guid.NewGuid()], DebitAccount = DebitIban },
                     new CreateBatchRequest { StatementIds = [Guid.NewGuid()] },
                     new CreateBatchRequest { PayoutIds = [Guid.NewGuid()], ExecutionDate = ExecutionDate, DebitAccount = DebitIban },
                 })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => controller.CreateBatch(retired));
            Assert.Equal(WireTransferErrors.FromStatementRetired, refused.Code);
            Assert.Equal("WireBatchFromStatementRetired", refused.MessageKey);
        }

        service.VerifyNoOtherCalls();

        // The request of today reaches the service with the payouts and the date; no date is the default date, which the service refuses.
        var payout = Guid.NewGuid();
        service.Setup(s => s.CreateBatchAsync(orgId, It.Is<IReadOnlyList<Guid>>(p => p.SequenceEqual(new[] { payout })), ExecutionDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new WireBatch { OrgId = orgId, Currency = "CHF", TotalAmount = 10m, TransferCount = 1, ExecutionDate = ExecutionDate, MessageFormat = WireMessageFormats.Pain001V09Ch03 }, "<Document/>"));
        service.Setup(s => s.CreateBatchAsync(orgId, It.IsAny<IReadOnlyList<Guid>>(), default, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(WireTransferErrors.ExecutionDateInvalid, "WireExecutionDateInvalid"));

        var created = Assert.IsType<OkObjectResult>((await controller.CreateBatch(new CreateBatchRequest { PayoutIds = [payout], ExecutionDate = ExecutionDate })).Result);
        var response = Assert.IsType<CreateBatchResponse>(created.Value);
        Assert.Equal("CHF", response.Currency);
        Assert.Equal(WireMessageFormats.Pain001V09Ch03, response.Format);
        Assert.Equal(ExecutionDate, response.ExecutionDate);
        var noDate = await Assert.ThrowsAsync<DomainRuleException>(() => controller.CreateBatch(new CreateBatchRequest { PayoutIds = [payout] }));
        Assert.Equal(WireTransferErrors.ExecutionDateInvalid, noDate.Code);
    }

    /// <summary>
    /// Privacy rule: the list of the batches shows the state, the currency, the number of transfers and the total, and never an IBAN, a name or the file. What the
    /// service returns and what the endpoint serializes carry none of them, the newest batch is first, and the batches of another org are not in the list.
    /// </summary>
    [Fact]
    public async Task WireBatch_List_ShowsStateCurrencyCountAndTotalWithoutIbans_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);
        var (chf, _) = await service.CreateBatchAsync(orgId, await AddTwoPayoutsAsync(db, orgId), ExecutionDate);
        await service.ConfirmDownloadAsync(orgId, chf.Id);
        var owner = await AddOwnerAsync(db, orgId, "Anna", "Neri", ItalianIban);
        var (eur, _) = await service.CreateBatchAsync(orgId, [await AddPayoutAsync(db, orgId, owner, 80.00m, "EUR")], ExecutionDate);
        db.WireBatches.Add(new WireBatch { OrgId = Guid.NewGuid(), Currency = "CHF", DebitAccount = DebitIban, TotalAmount = 1m, TransferCount = 1, XmlContent = "<Document/>" });
        await db.SaveChangesAsync();

        // The batches are told apart by their creation time, which the test clock does not move: order them as the list must.
        var stored = await db.WireBatches.SingleAsync(b => b.Id == eur.Id);
        stored.CreatedAt = Now.UtcDateTime.AddMinutes(5);
        await db.SaveChangesAsync();

        var list = await service.ListBatchesAsync(orgId);

        Assert.Equal(new[] { eur.Id, chf.Id }, list.Select(b => b.Id).ToArray());
        var listedChf = list.Single(b => b.Id == chf.Id);
        Assert.Equal("CHF", listedChf.Currency);
        Assert.Equal(WireBatchStatus.Downloaded, listedChf.Status);
        Assert.Equal(2, listedChf.TransferCount);
        Assert.Equal(2774.50m, listedChf.TotalAmount);
        Assert.Equal(WireMessageFormats.Pain001V09Ch03, listedChf.MessageFormat);
        Assert.Equal(ExecutionDate, listedChf.ExecutionDate);
        Assert.NotNull(listedChf.DownloadedAt);
        Assert.Null(listedChf.PaidAt);
        var listedEur = list.Single(b => b.Id == eur.Id);
        Assert.Equal(WireBatchStatus.Generated, listedEur.Status);
        Assert.Equal(WireMessageFormats.Pain001V03Sepa, listedEur.MessageFormat);

        var json = JsonSerializer.Serialize(list.Select(WireBatchListItemResponse.From).ToList(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var secret in Secrets.Concat(["<Document", "xml", "iban", "debit"]))
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);

        using var parsed = JsonDocument.Parse(json);
        var names = parsed.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(
            new[] { "createdAt", "currency", "downloadedAt", "executionDate", "format", "id", "paidAt", "status", "totalAmount", "transferCount" }.Order(),
            names);
    }

    // ---- the IBANs ------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The IBANs of the wires: normal form, check digits, the QR-IBAN (institution 30000 to 31999 of a Swiss or Liechtenstein IBAN) and the domestic IBAN of CHF.</summary>
    [Fact]
    public void WireIban_NormalFormCheckDigitsQrAndSwiss_Test()
    {
        Assert.Equal("CH9300762011623852957", WireIban.Normalize(" ch93 0076-2011 6238 5295 7 "));
        Assert.Equal(string.Empty, WireIban.Normalize(null));

        Assert.True(WireIban.IsValid(SwissIban));
        Assert.True(WireIban.IsValid(LiechtensteinIban));
        Assert.True(WireIban.IsValid(ItalianIban));
        Assert.False(WireIban.IsValid(BadIban));
        Assert.False(WireIban.IsValid("CH93"));
        Assert.False(WireIban.IsValid("ch9300762011623852957"));
        Assert.False(WireIban.IsValid("CH93007620116238529É7"));

        Assert.True(WireIban.IsQrIban(QrIban));
        Assert.True(WireIban.IsQrIban("CH0030000000000000000"));
        Assert.True(WireIban.IsQrIban("LI0031999000000000000"));
        Assert.False(WireIban.IsQrIban("CH0029999000000000000"));
        Assert.False(WireIban.IsQrIban("CH0032000000000000000"));
        Assert.False(WireIban.IsQrIban(SwissIban));
        Assert.False(WireIban.IsQrIban("DE0031999000000000000"));

        Assert.True(WireIban.IsSwiss(SwissIban));
        Assert.True(WireIban.IsSwiss(LiechtensteinIban));
        Assert.False(WireIban.IsSwiss(ItalianIban));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------------------------------

    private static string[] Names(XElement element)
    {
        return element.Elements().Select(e => e.Name.LocalName).ToArray();
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

    /// <summary>
    /// What a person who has to fix the writer needs when the file CasaZen writes does not validate against the official Swiss schema: the refusal (its code and
    /// arguments) and every error of the schema on the same file with its line, position and text. The file is written with a schema that accepts anything. Its
    /// data are the invented ones of these tests, so the text of an error may quote them without harm.
    /// </summary>
    private static async Task<string> ExplainRefusedFileAsync(DomainRuleException refused)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);
        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);
        var arguments = string.Join(" | ", refused.MessageArgs);
        var brokenRules = string.Join(", ", Pain001Checks.Violations(xml, WireTransferErrors.SwissCurrency, batch.TotalAmount, batch.TransferCount));

        var lines = new List<string>
        {
            $"The CHF file does not validate against its official schema: {refused.Code} ({arguments}).",
            "Errors of the schema on the file (line:position text):",
        };
        lines.AddRange(XsdValidator.Validate(xml, Pain001SchemaProvider.GetSchemas(WireTransferErrors.SwissCurrency)).Select(e => $"  {e.Line}:{e.Position} {e.Message}"));
        lines.Add($"Rules the file breaks beyond the schema: {brokenRules}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// A <c>[Fact]</c> that needs the official schema of a wire currency in the build: it is skipped, with the reason, while the file is not in the repository
/// (docs/runbooks/wire-transfers.md), and runs as soon as it is. Never a pass that checked nothing.
/// </summary>
public sealed class OfficialPain001SchemaFactAttribute : FactAttribute
{
    public OfficialPain001SchemaFactAttribute(string currency)
    {
        if (!Pain001SchemaProvider.HasOfficialSchema(currency))
        {
            Skip = $"The official {currency} wire schema is not in the repository (Casazen.Infrastructure/Payments/Schemas): add it as docs/runbooks/wire-transfers.md says, "
                   + $"and this proof validates the {currency} file against it. {Pain001SchemaProvider.LoadProblem(currency)}";
        }
    }
}
