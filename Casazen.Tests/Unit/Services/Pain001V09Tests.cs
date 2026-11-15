using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Core.Authorization;
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
/// and its endpoints check the organization. E16/F01/US02/T01 (NF-25): the EUR wire file is the ISO 20022 <c>pain.001.001.09</c> message with the rules of the SEPA
/// credit transfer of the EPC (service level <c>SEPA</c>, charge bearer <c>SLEV</c>, no BIC, structured addresses), validated the same way. The official schemas are
/// not in the repository until an operator adds them (docs/runbooks/wire-transfers.md): the proofs that need them are skipped with a clear message while they are
/// missing, and the other tests give the service a schema of their own (<see cref="Pain001TestSchemas"/>).
/// </summary>
public class Pain001V09Tests
{
    // The two messages are written in the same namespace: the schema each currency is validated against is what tells them apart.
    private static readonly XNamespace Swiss = Pain001SchemaProvider.SwissNamespace;
    private static readonly XNamespace Iso = Pain001SchemaProvider.IsoNamespace;

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
            Assert.Fail(await ExplainRefusedFileAsync(refused, WireTransferErrors.SwissCurrency));
            return;
        }

        var schemas = Pain001SchemaProvider.GetSchemas(WireTransferErrors.SwissCurrency);
        Assert.Empty(XsdValidator.Validate(xml, schemas));

        var altered = XDocument.Parse(xml);
        altered.Descendants(Swiss + "MsgId").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), schemas));
    }

    /// <summary>
    /// Money and data rule (E16/F01/US02/T01, NF-25): a EUR batch built from confirmed payouts is a <c>pain.001.001.09</c> file that validates against the OFFICIAL ISO
    /// 20022 schema <c>pain.001.001.09.xsd</c> of the message catalogue, with the schema of the build (no stand-in), and follows the SEPA rules the schema leaves open
    /// (the service has just checked them: <see cref="Pain001Checks"/>); an altered file does not validate. Skipped with its reason while the official file is not in
    /// <c>Payments/Schemas/Iso</c>: nothing is claimed valid that was not checked (the service answers 422 <c>wire_batch_schema_unavailable</c> then, never a made-up
    /// success).
    /// </summary>
    [OfficialPain001SchemaFact(WireTransferErrors.SepaCurrency)]
    public async Task Pain001V09_EurBatch_ValidAgainstIsoXsd_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId, WireTransferErrors.SepaCurrency);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance, null, Clock());

        WireBatch batch;
        string xml;
        try
        {
            (batch, xml) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);
        }
        catch (DomainRuleException refused)
        {
            // The writer has to change, not the schema: the failure says where (line and position, and the text of every error of the schema).
            Assert.Fail(await ExplainRefusedFileAsync(refused, WireTransferErrors.SepaCurrency));
            return;
        }

        var schemas = Pain001SchemaProvider.GetSchemas(WireTransferErrors.SepaCurrency);
        Assert.Empty(XsdValidator.Validate(xml, schemas));
        Assert.Empty(Pain001Checks.Violations(xml, WireTransferErrors.SepaCurrency, batch.TotalAmount, batch.TransferCount));
        Assert.Equal(WireMessageFormats.Pain001V09Sepa, batch.MessageFormat);

        var altered = XDocument.Parse(xml);
        altered.Descendants(Iso + "MsgId").Single().Remove();
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

        var ns = currency == "CHF" ? Swiss : Iso;
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
        Assert.All(new[] { "Mario Rossi", "Luigi Bianchi", "Via Cantonale", "Quokka Immobiliare SA" }, name => Assert.Contains(name, xml, StringComparison.Ordinal));
        Assert.True(EncryptedColumns.IsConfigured(db));
        var entity = db.Model.FindEntityType(typeof(WireBatch))!;
        foreach (var (property, clear) in new[] { (nameof(WireBatch.XmlContent), xml), (nameof(WireBatch.DebitAccount), DebitIban) })
        {
            Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(WireBatch) && c.Property == property && c.Purpose == EncryptedColumns.WireBatchFilePurpose);

            // What EF writes in the column: the payload of Data Protection, with none of the secrets of the file in it. The payload is random text, so a short value (a first name, a town) turns up in it by chance: only
            // values too long to be met by chance are searched for, the whole names and the whole street as the file holds them, and the payload is proved not to be the clear text.
            var converter = Assert.IsType<EncryptedStringConverter>(entity.FindProperty(property)!.GetValueConverter());
            var written = Assert.IsType<string>(converter.ConvertToProvider(clear));
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, written, StringComparison.Ordinal);
            Assert.NotEqual(clear, written);
            foreach (var secret in new[] { SwissIban, OtherSwissIban, DebitIban, "Mario Rossi", "Luigi Bianchi", "Via Cantonale", "Quokka Immobiliare SA", "<Document" })
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

        // The Swiss schema takes the bank of the debtor as a BIC or a clearing member and no «other identification»: the file names it by the institution identifier of
        // the debit IBAN (characters 5 to 9), a member of the Swiss bank clearing.
        var debtorBank = payment.Element(Swiss + "DbtrAgt")!.Element(Swiss + "FinInstnId")!;
        Assert.Equal(new[] { "ClrSysMmbId" }, Names(debtorBank));
        var clearingMember = debtorBank.Element(Swiss + "ClrSysMmbId")!;
        Assert.Equal(new[] { "ClrSysId", "MmbId" }, Names(clearingMember));
        Assert.Equal("CHBCC", clearingMember.Element(Swiss + "ClrSysId")!.Element(Swiss + "Cd")!.Value);
        Assert.Equal(DebitIban[4..9], clearingMember.Element(Swiss + "MmbId")!.Value);

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
    /// Data rule: the debtor agent of the Swiss file validates against the OFFICIAL Swiss schema, which takes a BIC or a clearing member id there and no «other
    /// identification»: a Swiss or a Liechtenstein debit IBAN names its bank by the institution identifier it carries (characters 5 to 9), and an IBAN that carries none
    /// (not a Swiss or Liechtenstein one) leaves the bank unnamed, which the schema also takes. Skipped with its reason while the official Swiss file is not in the build.
    /// </summary>
    [OfficialPain001SchemaFact(WireTransferErrors.SwissCurrency)]
    public void Pain001V09Writer_SwissDebtorAgent_IsTheInstitutionOfTheIban_OrNoBank_AndValidates_Test()
    {
        var schemas = Pain001SchemaProvider.GetSchemas(WireTransferErrors.SwissCurrency);
        var creditor = new Pain001Party("Mario Rossi", "Via Cantonale", "12", "6900", "Lugano", "CH");
        var debtor = new Pain001Party("Quokka Immobiliare SA", "Via Nassa", null, "6900", "Lugano", "CH");

        foreach (var (debitIban, institution) in new (string Iban, string? Institution)[] { (DebitIban, "08307"), (LiechtensteinIban, "08810"), (ItalianIban, null) })
        {
            var message = new Pain001Message(
                "CZ" + Guid.NewGuid().ToString("N"),
                new DateTime(2026, 11, 14, 11, 0, 0),
                "CHF",
                debtor,
                debitIban,
                [new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 10.10m, creditor, SwissIban, "Rendiconto 10/2026", ExecutionDate)]);

            var xml = Pain001V09Writer.Write(message);

            Assert.Empty(XsdValidator.Validate(xml, schemas));
            var bank = XDocument.Parse(xml).Descendants(Swiss + "DbtrAgt").Single().Element(Swiss + "FinInstnId")!;
            Assert.Null(bank.Element(Swiss + "Othr"));
            Assert.Equal(institution, bank.Element(Swiss + "ClrSysMmbId")?.Element(Swiss + "MmbId")?.Value);
            Assert.Equal(institution is null, !bank.HasElements);
        }
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

        // A text in an alphabet the message does not have is only points: it is no name and no address.
        Assert.True(Pain001Text.HasContent("É", 70));
        Assert.True(Pain001Text.HasContent("Via 3", 70));
        Assert.False(Pain001Text.HasContent("日本", 70));
        Assert.False(Pain001Text.HasContent("Иван Петров", 70));
        Assert.False(Pain001Text.HasContent(" / - . ", 70));
        Assert.False(Pain001Text.HasContent(null, 70));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Name = "Иван Петров" }));
        Assert.False(Pain001V09Writer.IsComplete(complete with { Street = "улица" }));
        Assert.True(Pain001V09Writer.IsComplete(complete with { Name = "Müller & Söhne AG" }));
    }

    // ---- the EUR file (E16/F01/US02/T01) --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Data rule (NF-25): the EUR file is the ISO 20022 <c>pain.001.001.09</c> message with the rules of the SEPA credit transfer of the EPC, and nothing else: its
    /// namespace and the schema location of the ISO file, one payment with the service level <c>SEPA</c> (before the requested date) and the charge bearer <c>SLEV</c>
    /// (after the debtor agent), the debtor agent «not provided» and no creditor agent and no BIC at all (the IBAN alone: the BIC is optional), the debtor and the
    /// creditors with a <b>structured</b> address (and no address line), the creditor IBAN and the remittance «Rendiconto 10/2026». The children of every element follow
    /// the ISO 20022 sequence, which a schema checks and this test keeps checked while the official file is missing.
    /// </summary>
    [Fact]
    public async Task Pain001V09_EurFile_IsTheSepaMessageWithStructuredAddresses_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId, "EUR");

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);

        var document = XDocument.Parse(xml);
        Assert.Equal(Iso + "Document", document.Root!.Name);
        Assert.Equal(
            Pain001SchemaProvider.IsoNamespace + " pain.001.001.09.xsd",
            (string?)document.Root.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "schemaLocation"));
        var initiation = Assert.Single(document.Root.Elements());
        Assert.Equal("CstmrCdtTrfInitn", initiation.Name.LocalName);
        Assert.Equal(new[] { "GrpHdr", "PmtInf" }, Names(initiation));

        var header = initiation.Element(Iso + "GrpHdr")!;
        Assert.Equal(new[] { "MsgId", "CreDtTm", "NbOfTxs", "CtrlSum", "InitgPty" }, Names(header));
        Assert.Equal("CZ" + batch.Id.ToString("N"), header.Element(Iso + "MsgId")!.Value);
        Assert.Equal("2026-11-14T11:00:00", header.Element(Iso + "CreDtTm")!.Value);
        Assert.Equal("2", header.Element(Iso + "NbOfTxs")!.Value);
        Assert.Equal("2774.50", header.Element(Iso + "CtrlSum")!.Value);
        Assert.Equal(new[] { "Nm" }, Names(header.Element(Iso + "InitgPty")!));

        // One payment: the service level comes before the requested date and the charge bearer after the debtor agent, as the sequence of the ISO message has them.
        var payment = initiation.Element(Iso + "PmtInf")!;
        Assert.Equal(
            new[] { "PmtInfId", "PmtMtd", "BtchBookg", "NbOfTxs", "CtrlSum", "PmtTpInf", "ReqdExctnDt", "Dbtr", "DbtrAcct", "DbtrAgt", "ChrgBr", "CdtTrfTxInf", "CdtTrfTxInf" },
            Names(payment));
        Assert.True(payment.Element(Iso + "PmtInfId")!.Value.Length <= 35);
        Assert.Equal("TRF", payment.Element(Iso + "PmtMtd")!.Value);
        Assert.Equal("2", payment.Element(Iso + "NbOfTxs")!.Value);
        Assert.Equal("2774.50", payment.Element(Iso + "CtrlSum")!.Value);
        var paymentType = payment.Element(Iso + "PmtTpInf")!;
        Assert.Equal(new[] { "SvcLvl" }, Names(paymentType));
        Assert.Equal(new[] { "Cd" }, Names(paymentType.Element(Iso + "SvcLvl")!));
        Assert.Equal("SEPA", paymentType.Element(Iso + "SvcLvl")!.Element(Iso + "Cd")!.Value);
        Assert.Equal("SLEV", payment.Element(Iso + "ChrgBr")!.Value);
        Assert.Equal("2026-11-16", payment.Element(Iso + "ReqdExctnDt")!.Element(Iso + "Dt")!.Value);

        // The debtor is the agency with its structured address; the BIC is optional and the file has none.
        var debtor = payment.Element(Iso + "Dbtr")!;
        Assert.Equal(new[] { "Nm", "PstlAdr" }, Names(debtor));
        Assert.Equal("Quokka Immobiliare SA", debtor.Element(Iso + "Nm")!.Value);
        Assert.Equal(new[] { "StrtNm", "BldgNb", "PstCd", "TwnNm", "Ctry" }, Names(debtor.Element(Iso + "PstlAdr")!));
        Assert.Equal(DebitIban, payment.Element(Iso + "DbtrAcct")!.Element(Iso + "Id")!.Element(Iso + "IBAN")!.Value);
        Assert.Equal("NOTPROVIDED", payment.Element(Iso + "DbtrAgt")!.Element(Iso + "FinInstnId")!.Element(Iso + "Othr")!.Element(Iso + "Id")!.Value);
        Assert.Empty(document.Descendants(Iso + "BICFI"));
        Assert.Empty(document.Descendants(Iso + "CdtrAgt"));

        var transfers = payment.Elements(Iso + "CdtTrfTxInf").ToList();
        Assert.Equal(payouts.Select(WireBatchPayout.EndToEndIdOf).Order(), transfers.Select(t => t.Element(Iso + "PmtId")!.Element(Iso + "EndToEndId")!.Value).Order());
        Assert.Equal(
            new[] { ItalianIban, OtherItalianIban }.Order(),
            transfers.Select(t => t.Element(Iso + "CdtrAcct")!.Element(Iso + "Id")!.Element(Iso + "IBAN")!.Value).Order());
        Assert.Equal(
            new[] { "Luigi Bianchi", "Mario Rossi" },
            transfers.Select(t => t.Element(Iso + "Cdtr")!.Element(Iso + "Nm")!.Value).Order(StringComparer.Ordinal).ToArray());
        foreach (var transfer in transfers)
        {
            Assert.Equal(new[] { "PmtId", "Amt", "Cdtr", "CdtrAcct", "RmtInf" }, Names(transfer));
            Assert.Equal("EUR", (string?)transfer.Element(Iso + "Amt")!.Element(Iso + "InstdAmt")!.Attribute("Ccy"));
            var creditor = transfer.Element(Iso + "Cdtr")!;
            Assert.Equal(new[] { "Nm", "PstlAdr" }, Names(creditor));
            var address = creditor.Element(Iso + "PstlAdr")!;
            Assert.Equal(new[] { "StrtNm", "BldgNb", "PstCd", "TwnNm", "Ctry" }, Names(address));
            Assert.Equal(new[] { "Via Volta", "8", "22100", "Como", "IT" }, address.Elements().Select(e => e.Value).ToArray());
            Assert.Equal("Rendiconto 10/2026", transfer.Element(Iso + "RmtInf")!.Element(Iso + "Ustrd")!.Value);
        }

        Assert.DoesNotContain("AdrLine", xml);
        Assert.Equal("pain.001.001.09", batch.MessageFormat);
        Assert.Equal(WireMessageFormats.Pain001V09Sepa, batch.MessageFormat);
        Assert.Equal(ExecutionDate, batch.ExecutionDate);
        Assert.Empty(Pain001Checks.Violations(xml, "EUR", batch.TotalAmount, batch.TransferCount));
        Assert.Empty(XsdValidator.Validate(xml, Pain001TestSchemas.Accepting()));
    }

    /// <summary>
    /// The writer keeps the rules of the SEPA message for every payment it writes, whatever the number of execution dates: two dates are two payments in date order, each
    /// with the service level <c>SEPA</c>, the charge bearer <c>SLEV</c>, its own id, count and control sum, and the file follows its own rules (<see cref="Pain001Checks"/>).
    /// A currency that has no wire file has no message to write, and the message of a currency is told by <see cref="WireMessageFormats.Of"/>.
    /// </summary>
    [Fact]
    public void Pain001V09Writer_EurPaymentsHaveTheSepaRules_Test()
    {
        var creditor = new Pain001Party("Mario Rossi", "Via Volta", "8", "22100", "Como", "it");
        var debtor = new Pain001Party("Quokka Immobiliare SA", "Via Nassa", null, "6900", "Lugano", "CH");
        var message = new Pain001Message(
            "CZ" + Guid.NewGuid().ToString("N"),
            new DateTime(2026, 11, 14, 11, 0, 0),
            "EUR",
            debtor,
            DebitIban,
            [
                new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 10.10m, creditor, ItalianIban, "Rendiconto 10/2026", new DateOnly(2026, 11, 20)),
                new Pain001Transfer("CZ" + Guid.NewGuid().ToString("N"), 20.20m, creditor, ItalianIban, "Rendiconto 10/2026", new DateOnly(2026, 11, 16)),
            ]);

        var xml = Pain001V09Writer.Write(message);

        var payments = XDocument.Parse(xml).Descendants(Iso + "PmtInf").ToList();
        Assert.Equal(2, payments.Count);
        Assert.Equal(new[] { "2026-11-16", "2026-11-20" }, payments.Select(p => p.Element(Iso + "ReqdExctnDt")!.Element(Iso + "Dt")!.Value).ToArray());
        Assert.All(payments, p => Assert.Equal("SEPA", p.Element(Iso + "PmtTpInf")!.Element(Iso + "SvcLvl")!.Element(Iso + "Cd")!.Value));
        Assert.All(payments, p => Assert.Equal("SLEV", p.Element(Iso + "ChrgBr")!.Value));
        Assert.Equal(2, payments.Select(p => p.Element(Iso + "PmtInfId")!.Value).Distinct().Count());
        Assert.Equal(new[] { "20.20", "10.10" }, payments.Select(p => p.Element(Iso + "CtrlSum")!.Value).ToArray());
        Assert.Equal("IT", XDocument.Parse(xml).Descendants(Iso + "Cdtr").First().Descendants(Iso + "Ctry").Single().Value);
        Assert.Empty(Pain001Checks.Violations(xml, "EUR", 30.30m, 2));

        Assert.Throws<ArgumentException>(() => Pain001V09Writer.Write(message with { Currency = "USD" }));
        Assert.Equal(WireMessageFormats.Pain001V09Sepa, WireMessageFormats.Of(" eur "));
        Assert.Equal(WireMessageFormats.Pain001V09Ch03, WireMessageFormats.Of("CHF"));
        Assert.Null(WireMessageFormats.Of("USD"));
    }

    /// <summary>
    /// Data rule: the texts of the EUR file are the Latin characters of the SEPA messages, letters, digits and <c>/ - ? : ( ) . , ' +</c> only. An accented letter loses its
    /// accent, the letters that have no base letter are transcribed (<c>ß</c> is <c>ss</c>, <c>Æ</c> is <c>AE</c>, <c>Ø</c> is <c>O</c>), the ampersand is the plus sign and what
    /// has no equivalent is a point; no character of Latin-1 or of the Latin extensions comes out of it outside that set, and a name or an address that is nothing but
    /// points and signs is no name and no address. The Swiss texts are as they were: they keep the letters of Latin-1 with their accent and the ampersand.
    /// </summary>
    [Fact]
    public void Pain001Text_Clean_SepaCharacters_AreTheLatinSetOfTheEpc_Test()
    {
        const Pain001Flavor sepa = Pain001Flavor.Sepa;

        Assert.Equal("Muller + Sohne AG", Pain001Text.Clean("  Müller &  Söhne AG ", 70, sepa));
        Assert.Equal("Strasse AEsop Oyvind", Pain001Text.Clean("Straße Æsop Øyvind", 70, sepa));
        Assert.Equal("Via dell'Universita 12/A (c/o) Peru, Caffe", Pain001Text.Clean("Via dell'Università 12/A (c/o) Perù, Caffè", 70, sepa));
        Assert.Equal("Lukasz Zolc", Pain001Text.Clean("Łukasz Żółć", 70, sepa));
        Assert.Equal("..", Pain001Text.Clean("日本", 70, sepa));
        Assert.Equal("Rendiconto 10/2026", Pain001Text.Clean("Rendiconto 10/2026", 140, sepa));

        // A transcription makes a text longer; the element has its length all the same.
        Assert.Equal("sssss", Pain001Text.Clean("ßßßß", 5, sepa));

        // The Swiss message is as it was.
        Assert.Equal("Müller & Söhne AG", Pain001Text.Clean("  Müller &  Söhne AG ", 70));
        Assert.Equal("Straße Æsop Øyvind", Pain001Text.Clean("Straße Æsop Øyvind", 70, Pain001Flavor.Swiss));

        // Whatever the character, what the SEPA message writes is in the set of the EPC.
        var epcSet = new Regex("^[A-Za-z0-9/?:().,'+ -]*$", RegexOptions.CultureInvariant);
        for (var code = 0x20; code <= 0x24F; code++)
        {
            var written = Pain001Text.Clean("a" + char.ConvertFromUtf32(code) + "b", 70, sepa);
            Assert.True(epcSet.IsMatch(written), $"U+{code:X4} is written outside the SEPA character set");
        }

        Assert.True(Pain001Text.HasContent("Øß", 70, sepa));
        Assert.False(Pain001Text.HasContent("&", 70, sepa));
        Assert.False(Pain001Text.HasContent("Иван Петров", 70, sepa));
        var party = new Pain001Party("Müller & Söhne AG", "Straße 3", null, "8001", "Zürich", "CH");
        Assert.True(Pain001V09Writer.IsComplete(party, sepa));
        Assert.False(Pain001V09Writer.IsComplete(party with { Name = "Иван Петров" }, sepa));
        Assert.False(Pain001V09Writer.IsComplete(party with { Country = "Svizzera" }, sepa));
    }

    /// <summary>
    /// Data rule (NF-25): the SEPA rules of the EUR file that the ISO schema leaves open are checked on the finished file, by the name of the element that breaks them and
    /// never by a value: a payment without the service level <c>SEPA</c> or with another one, without the charge bearer <c>SLEV</c> or with another one, a debtor or a creditor
    /// with no postal address, with an address of free lines alone (the unstructured form), without a country, or with more than two free lines.
    /// </summary>
    [Theory]
    [InlineData("no-service-level", "SvcLvl")]
    [InlineData("other-service-level", "SvcLvl")]
    [InlineData("no-charge-bearer", "ChrgBr")]
    [InlineData("other-charge-bearer", "ChrgBr")]
    [InlineData("debtor-without-address", "PstlAdr")]
    [InlineData("creditor-without-address", "PstlAdr")]
    [InlineData("creditor-unstructured-address", "PstlAdr")]
    [InlineData("creditor-without-country", "PstlAdr")]
    [InlineData("creditor-three-address-lines", "PstlAdr")]
    public async Task Pain001Checks_SepaRulesOfTheEurFile_AreCheckedByTheNameOfTheElement_Test(string mutation, string rule)
    {
        var (batch, xml) = await WriteTwoEurTransfersAsync();
        Assert.Empty(Pain001Checks.Violations(xml, "EUR", batch.TotalAmount, batch.TransferCount));

        var violations = Pain001Checks.Violations(MutateSepaFile(xml, mutation), "EUR", batch.TotalAmount, batch.TransferCount);

        Assert.Equal(new[] { rule }, violations);
        Assert.All(violations, v => Assert.DoesNotContain(v, Secrets));
    }

    /// <summary>
    /// A hybrid address (a town, a country and up to two free lines) is as good as a structured one for the SEPA rules, and the BIC is optional: a creditor agent with a BIC is
    /// no violation, as the absence of one is none. The rules are the EUR file's own: the Swiss file has no service level and no charge bearer and none is asked of it.
    /// </summary>
    [Fact]
    public async Task Pain001Checks_HybridAddressAndAnOptionalBic_AreSepaToo_AndTheSwissFileIsNotAsked_Test()
    {
        var (batch, xml) = await WriteTwoEurTransfersAsync();
        var document = XDocument.Parse(xml);
        var transfer = document.Descendants(Iso + "CdtTrfTxInf").First();
        var address = transfer.Element(Iso + "Cdtr")!.Element(Iso + "PstlAdr")!;
        address.RemoveNodes();
        address.Add(new XElement(Iso + "TwnNm", "Como"), new XElement(Iso + "Ctry", "IT"), new XElement(Iso + "AdrLine", "Via Volta 8"), new XElement(Iso + "AdrLine", "22100 Como"));
        transfer.Element(Iso + "Cdtr")!.AddBeforeSelf(new XElement(Iso + "CdtrAgt", new XElement(Iso + "FinInstnId", new XElement(Iso + "BICFI", "BCITITMM"))));

        Assert.Empty(Pain001Checks.Violations(document.ToString(), "EUR", batch.TotalAmount, batch.TransferCount));

        // The same Swiss payment, written with none of them, breaks no rule of its own file.
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var (chfBatch, chfXml) = await NewService(db).CreateBatchAsync(orgId, await AddTwoPayoutsAsync(db, orgId), ExecutionDate);
        Assert.DoesNotContain("SvcLvl", chfXml);
        Assert.DoesNotContain("ChrgBr", chfXml);
        Assert.Empty(Pain001Checks.Violations(chfXml, "CHF", chfBatch.TotalAmount, chfBatch.TransferCount));
    }

    /// <summary>
    /// Data rule (E16/F01/US02/T01): the EUR file writes the structured address of the creditor, so an owner paid in EUR with no complete address is refused for the batch
    /// with 422 <c>wire_owner_address_missing</c> and the owner id (the file of before wrote the name alone), never a name or an IBAN, and nothing is saved; once the address
    /// is complete the same request makes the batch.
    /// </summary>
    [Fact]
    public async Task WireBatch_EurOwnerWithoutAddress_IsRefusedWithTheOwnerId_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var fine = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", OtherItalianIban, address: ComoAddress);
        var without = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ItalianIban, withAddress: false);
        var payouts = new List<Guid>
        {
            await AddPayoutAsync(db, orgId, fine, 100.00m, "EUR"),
            await AddPayoutAsync(db, orgId, without, 200.00m, "EUR"),
        };
        var logger = new CapturingLogger<WireTransferService>();
        var service = NewService(db, logger);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBatchAsync(orgId, payouts, ExecutionDate));

        Assert.Equal(WireTransferErrors.OwnerAddressMissing, refused.Code);
        Assert.Equal(without, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["ownerIds"])));
        AssertNoSecret(refused, logger);
        await AssertNothingSavedAsync(db);

        var owner = await db.PropertyOwners.SingleAsync(o => o.Id == without);
        (owner.BillingStreet, owner.BillingCivic, owner.BillingPostalCode, owner.BillingCity, owner.BillingCountry) = ComoAddress;
        await db.SaveChangesAsync();
        var (batch, _) = await service.CreateBatchAsync(orgId, payouts, ExecutionDate);
        Assert.Equal(2, batch.TransferCount);
    }

    /// <summary>
    /// Data rule: a EUR batch made before the EUR file moved to <c>pain.001.001.09</c> keeps the file it was made with, which is the evidence of what was uploaded to the
    /// bank: it is listed with its message (<c>pain.001.001.03</c>) and handed out byte for byte as it was stored, and it is never written again or validated again
    /// against the schema of the new message.
    /// </summary>
    [Fact]
    public async Task WireBatch_EurBatchOfBeforeTheMove_KeepsItsStoredFile_Test()
    {
        const string stored = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pain.001.001.03\"><CstmrCdtTrfInitn/></Document>";
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        var batch = new WireBatch
        {
            OrgId = orgId,
            Currency = "EUR",
            DebitAccount = DebitIban,
            TotalAmount = 10m,
            TransferCount = 1,
            MessageFormat = WireMessageFormats.Pain001V03Sepa,
            XmlContent = stored,
        };
        db.WireBatches.Add(batch);
        await db.SaveChangesAsync();
        var service = NewService(db, null, _ => throw new InvalidOperationException("a stored file is not validated again"));

        var (_, file) = await service.GetBatchFileAsync(orgId, batch.Id);
        var listed = Assert.Single(await service.ListBatchesAsync(orgId));

        Assert.Equal(stored, file);
        Assert.Equal("pain.001.001.03", listed.MessageFormat);
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
    /// 422 <c>wire_owner_iban_not_swiss</c> (not a domestic payment), while a Liechtenstein IBAN is a domestic one; in a EUR batch an Italian IBAN is fine (and a Swiss
    /// one too: Switzerland is in the SEPA area) and an IBAN outside the SEPA area is 422 <c>wire_owner_iban_not_sepa</c> (the SEPA credit transfer cannot reach it);
    /// a QR-IBAN is refused in EUR as well.
    /// </summary>
    [Theory]
    [InlineData("CHF", QrIban, WireTransferErrors.OwnerQrIban)]
    [InlineData("CHF", BadIban, WireTransferErrors.OwnerIbanInvalid)]
    [InlineData("CHF", ItalianIban, WireTransferErrors.OwnerIbanNotSwiss)]
    [InlineData("EUR", WrongLengthItalianIban, WireTransferErrors.OwnerIbanInvalid)]
    [InlineData("EUR", QrIban, WireTransferErrors.OwnerQrIban)]
    [InlineData("EUR", NonSepaIban, WireTransferErrors.OwnerIbanNotSepa)]
    [InlineData("CHF", LiechtensteinIban, null)]
    [InlineData("EUR", ItalianIban, null)]
    [InlineData("EUR", SwissIban, null)]
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

    /// <summary>
    /// Data rule: an owner whose name is only in an alphabet the message cannot write (Cyrillic, Greek, Chinese: it would go to the bank as points) is refused for the
    /// batch like an owner with no name, with the owner id (422 <c>wire_owner_address_missing</c>), in both files, and nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("CHF", SwissIban)]
    [InlineData("EUR", ItalianIban)]
    public async Task WireBatch_OwnerWithANameInAnotherAlphabet_IsRefusedWithTheOwnerId_Test(string currency, string iban)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Иван", "Петров", iban);
        var payout = await AddPayoutAsync(db, orgId, owner, 500.00m, currency);
        var logger = new CapturingLogger<WireTransferService>();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => NewService(db, logger).CreateBatchAsync(orgId, [payout], ExecutionDate));

        Assert.Equal(WireTransferErrors.OwnerAddressMissing, refused.Code);
        Assert.Equal(owner, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["ownerIds"])));
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

    /// <summary>
    /// Money rule: a payout reversed after its batch was made is still in the batch (a reversal posts the opposite ledger lines and does not know about the
    /// batches), so the file would pay it. A batch that is not paid yet is then neither handed out, nor confirmed as downloaded, nor marked as paid (422
    /// <c>wire_batch_payouts_reversed</c>, with the ids of the payouts) and its state does not move; a batch that was paid stays readable as evidence.
    /// </summary>
    [Fact]
    public async Task WireBatch_PayoutReversedAfterTheBatch_IsNeitherDownloadedNorMarkedPaid_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);

        // A batch that was made and downloaded, and then one of its payouts is reversed: its file is the one that would pay it.
        var downloaded = await AddTwoPayoutsAsync(db, orgId);
        var (batch, _) = await service.CreateBatchAsync(orgId, downloaded, ExecutionDate);
        await service.ConfirmDownloadAsync(orgId, batch.Id);
        await ReverseAsync(db, downloaded[0]);

        var file = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetBatchFileAsync(orgId, batch.Id));
        var paid = await Assert.ThrowsAsync<DomainRuleException>(() => service.ConfirmPaidAsync(orgId, batch.Id));
        Assert.Equal(WireTransferErrors.BatchPayoutsReversed, file.Code);
        Assert.Equal(WireTransferErrors.BatchPayoutsReversed, paid.Code);
        Assert.Equal("WireBatchPayoutsReversed", paid.MessageKey);
        Assert.Equal(1, (int)Assert.Single(paid.MessageArgs));
        Assert.Equal(downloaded[0], Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(paid.Extensions["payoutIds"])));
        var stillDownloaded = await db.WireBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id);
        Assert.Equal(WireBatchStatus.Downloaded, stillDownloaded.Status);
        Assert.Null(stillDownloaded.PaidAt);

        // A batch not downloaded yet: the file is not handed out, the confirmation of the download is refused, and it stays as it was.
        var fresh = await AddTwoPayoutsAsync(db, orgId);
        var (generated, _) = await service.CreateBatchAsync(orgId, fresh, ExecutionDate);
        await ReverseAsync(db, fresh[1]);

        var freshFile = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetBatchFileAsync(orgId, generated.Id));
        var freshDownload = await Assert.ThrowsAsync<DomainRuleException>(() => service.ConfirmDownloadAsync(orgId, generated.Id));
        Assert.Equal(WireTransferErrors.BatchPayoutsReversed, freshFile.Code);
        Assert.Equal(WireTransferErrors.BatchPayoutsReversed, freshDownload.Code);
        var untouched = await db.WireBatches.AsNoTracking().SingleAsync(b => b.Id == generated.Id);
        Assert.Equal(WireBatchStatus.Generated, untouched.Status);
        Assert.Null(untouched.DownloadedAt);

        // A batch that was paid before the reversal is the evidence of what was sent: its file stays readable.
        var sent = await AddTwoPayoutsAsync(db, orgId);
        var (settled, xml) = await service.CreateBatchAsync(orgId, sent, ExecutionDate);
        await service.ConfirmDownloadAsync(orgId, settled.Id);
        await service.ConfirmPaidAsync(orgId, settled.Id);
        await ReverseAsync(db, sent[0]);

        Assert.Equal(xml, (await service.GetBatchFileAsync(orgId, settled.Id)).XmlContent);
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
        var controller = new WireTransferController(service.Object, resolver.Object, Mock.Of<IHostScopeResolver>(), NullLogger<WireTransferController>.Instance)
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
        Assert.Equal(WireMessageFormats.Pain001V09Sepa, listedEur.MessageFormat);

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

        // The length of the country is part of the check, as for the owners and the issuer (the same definition): an Italian IBAN of 26 characters (Italy has 27)
        // whose check digits are right is no IBAN.
        Assert.False(WireIban.IsValid(WrongLengthItalianIban));
        Assert.Equal("IT90X054281110100000012345", WireIban.Normalize(" it90 X054 2811 1010 0000 0123 45"));

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

    /// <summary>What <c>OwnerPayoutService.ReversePayoutAsync</c> does to the payout row (it does not know about the wire batches).</summary>
    private static async Task ReverseAsync(AppDbContext db, Guid payoutId)
    {
        var payout = await db.OwnerPayouts.SingleAsync(p => p.Id == payoutId);
        payout.Status = OwnerPayoutStatus.Reversed;
        payout.ReversedAt = Now.UtcDateTime;
        payout.ReversalReason = "reversed after the batch";
        await db.SaveChangesAsync();
    }

    private static string[] Names(XElement element)
    {
        return element.Elements().Select(e => e.Name.LocalName).ToArray();
    }

    /// <summary>A EUR batch of two transfers to owners of Como and its SEPA file.</summary>
    private static async Task<(WireBatch Batch, string Xml)> WriteTwoEurTransfersAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId, WireTransferErrors.SepaCurrency);

        return await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);
    }

    /// <summary>The EUR file with one rule of the SEPA credit transfer broken, as the name of the mutation says.</summary>
    private static string MutateSepaFile(string xml, string mutation)
    {
        var document = XDocument.Parse(xml);
        var payment = document.Descendants(Iso + "PmtInf").Single();
        var creditorAddress = document.Descendants(Iso + "Cdtr").First().Element(Iso + "PstlAdr")!;

        switch (mutation)
        {
            case "no-service-level": payment.Element(Iso + "PmtTpInf")!.Remove(); break;
            case "other-service-level": payment.Element(Iso + "PmtTpInf")!.Element(Iso + "SvcLvl")!.Element(Iso + "Cd")!.Value = "URGP"; break;
            case "no-charge-bearer": payment.Element(Iso + "ChrgBr")!.Remove(); break;
            case "other-charge-bearer": payment.Element(Iso + "ChrgBr")!.Value = "SHAR"; break;
            case "debtor-without-address": payment.Element(Iso + "Dbtr")!.Element(Iso + "PstlAdr")!.Remove(); break;
            case "creditor-without-address": creditorAddress.Remove(); break;
            case "creditor-unstructured-address":
                creditorAddress.RemoveNodes();
                creditorAddress.Add(new XElement(Iso + "AdrLine", "Via Volta 8"), new XElement(Iso + "AdrLine", "22100 Como"));
                break;
            case "creditor-without-country": creditorAddress.Element(Iso + "Ctry")!.Remove(); break;
            case "creditor-three-address-lines":
                creditorAddress.Add(new XElement(Iso + "AdrLine", "a"), new XElement(Iso + "AdrLine", "b"), new XElement(Iso + "AdrLine", "c"));
                break;
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

    /// <summary>
    /// What a person who has to fix the writer needs when the file CasaZen writes does not validate against the official schema of its currency (the Swiss one for
    /// CHF, the ISO one for EUR): the refusal (its code and arguments) and every error of the schema on the same file with its line, position and text. The file is
    /// written with a schema that accepts anything. Its data are the invented ones of these tests, so the text of an error may quote them without harm.
    /// </summary>
    private static async Task<string> ExplainRefusedFileAsync(DomainRuleException refused, string currency)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId, currency);
        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, payouts, ExecutionDate);
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
