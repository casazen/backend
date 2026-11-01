using System.Text;
using Casazen.Core.BankStatements;
using Casazen.Core.Exceptions;
using Xunit;

namespace Casazen.Tests.Unit.BankStatements;

/// <summary>
/// The statement of the bank in each of the formats the importer reads, from the text a bank really writes: CSV with a preamble and
/// decimal commas, CSV in Windows-1252, «Dare/Avere», CAMT.053 (two versions), MT940 (three ways to write the information), and the files it
/// refuses. The parser is a pure function: bytes in, credits out.
/// </summary>
public class BankStatementParserTests
{
    private const string PayerIban = "DE89370400440532013000";

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    // ---------------------------------------------------------------- CSV

    [Fact]
    public void Csv_AnItalianExportWithAPreambleAndDecimalCommas_ReadsTheCreditsOnly()
    {
        const string csv =
            "Conto;IT60X0542811101000000123456\r\n"
            + "Periodo;01/10/2026 - 31/10/2026\r\n"
            + "\r\n"
            + "Data contabile;Data valuta;Descrizione;Importo;Divisa\r\n"
            + "05/10/2026;05/10/2026;\"BONIFICO A VOSTRO FAVORE DA VERDI GIULIA CANONE OTTOBRE 2026\";\"900,00\";EUR\r\n"
            + "06/10/2026;06/10/2026;PAGAMENTO POS;-45,50;EUR\r\n"
            + "10/10/2026;11/10/2026;\"BONIFICO DA ROSSI MARIO\r\nIBAN IT60X0542811101000000123456 AFFITTO\";1.250,00;EUR\r\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(BankStatementFormat.Csv, statement.Format);
        Assert.Equal(3, statement.MovementCount);
        Assert.Equal(2, statement.Credits.Count);

        var first = statement.Credits[0];
        Assert.Equal(new DateOnly(2026, 10, 5), first.BookingDate);
        Assert.Equal(new DateOnly(2026, 10, 5), first.ValueDate);
        Assert.Equal(900.00m, first.Amount);
        Assert.Equal("EUR", first.Currency);
        Assert.Contains("VERDI GIULIA", first.Description, StringComparison.Ordinal);

        var second = statement.Credits[1];
        Assert.Equal(new DateOnly(2026, 10, 10), second.BookingDate);
        Assert.Equal(new DateOnly(2026, 10, 11), second.ValueDate);
        Assert.Equal(1250.00m, second.Amount);
        Assert.Equal("IT60X0542811101000000123456", second.CounterpartyIban);
    }

    [Fact]
    public void Csv_DareAndAvereInTwoColumns_TakesTheMoneyIn()
    {
        const string csv =
            "Data;Valuta;Causale;Dare;Avere\n"
            + "01/10/2026;01/10/2026;Canone ottobre;;900,00\n"
            + "02/10/2026;02/10/2026;Bolletta luce;55,00;\n"
            + "03/10/2026;03/10/2026;Rimborso;-10,00;\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(3, statement.MovementCount);
        var credit = Assert.Single(statement.Credits);
        Assert.Equal(900.00m, credit.Amount);
        Assert.Equal("Canone ottobre", credit.Description);
    }

    [Fact]
    public void Csv_CommasAsDelimiterAndPointsAsDecimals_IsReadAsWell()
    {
        const string csv =
            "Date,Description,Amount\n"
            + "2026-10-03,\"Rent Verdi, October\",900.00\n"
            + "2026-10-04,Coffee,-3.50\n"
            + "2026-10-05,Deposit,\"1,250.00\"\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(new[] { 900.00m, 1250.00m }, statement.Credits.Select(c => c.Amount).ToArray());
        Assert.Equal("Rent Verdi, October", statement.Credits[0].Description);
    }

    [Fact]
    public void Csv_ASignColumn_MakesTheDebitsNegative()
    {
        const string csv =
            "Data;Descrizione;Importo;Segno\n"
            + "03/10/2026;Canone;900,00;A\n"
            + "04/10/2026;Pagamento;50,00;D\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        var credit = Assert.Single(statement.Credits);
        Assert.Equal(900.00m, credit.Amount);
        Assert.Equal(2, statement.MovementCount);
    }

    [Fact]
    public void Csv_ASignColumnOfJustPlusAndMinus_DecidesWhichAreCredits()
    {
        // The amount carries no sign of its own: an unread «-» would make the refund of a deposit look like a rent that came in.
        const string csv =
            "Data;Descrizione;Importo;Segno\n"
            + "01/10/2026;Canone ottobre;900,00;+\n"
            + "02/10/2026;Restituzione cauzione Rossi;800,00;-\n"
            + "03/10/2026;Spese;50,00;–\n"
            + "04/10/2026;Rimborso;60,00;−\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(4, statement.MovementCount);
        Assert.Equal(900.00m, Assert.Single(statement.Credits).Amount);
    }

    [Fact]
    public void Csv_ATypeOfMovementColumn_SaysWhatCameInAndWhatWentOut()
    {
        const string csv =
            "Data;Descrizione;Importo;Tipo movimento\n"
            + "01/10/2026;Canone ottobre;900,00;Entrata\n"
            + "02/10/2026;Restituzione cauzione Rossi;800,00;Uscita\n";

        var credit = Assert.Single(BankStatementParser.Parse(Utf8(csv)).Credits);

        Assert.Equal(900.00m, credit.Amount);
    }

    [Fact]
    public void Csv_ACreditInAnotherCurrency_IsNotKept()
    {
        const string csv =
            "Data;Descrizione;Importo;Divisa\n"
            + "03/10/2026;Canone;900,00;EUR\n"
            + "04/10/2026;Transfer;900,00;USD\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(2, statement.MovementCount);
        Assert.Equal("EUR", Assert.Single(statement.Credits).Currency);
    }

    [Fact]
    public void Csv_InWindows1252_KeepsTheAccents()
    {
        var windows = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        var bytes = windows.GetBytes("Data;Descrizione;Importo\r\n03/10/2026;Perché è il canone di Niccolò;900,00\r\n");

        var statement = BankStatementParser.Parse(bytes);

        Assert.Equal("Perché è il canone di Niccolò", Assert.Single(statement.Credits).Description);
    }

    [Fact]
    public void Csv_WithAByteOrderMark_ReadsTheFirstTitle()
    {
        var bytes = new UTF8Encoding(true).GetPreamble()
            .Concat(Utf8("Data;Descrizione;Importo\r\n03/10/2026;Canone;900,00\r\n"))
            .ToArray();

        var statement = BankStatementParser.Parse(bytes);

        Assert.Single(statement.Credits);
    }

    [Fact]
    public void Csv_RowsThatAreNotMovements_AreSkipped()
    {
        const string csv =
            "Data;Descrizione;Importo\n"
            + "Saldo iniziale;;1.000,00\n"
            + "03/10/2026;Canone;900,00\n"
            + "Totale;;900,00\n";

        var statement = BankStatementParser.Parse(Utf8(csv));

        Assert.Equal(1, statement.MovementCount);
        Assert.Single(statement.Credits);
    }

    [Fact]
    public void Csv_OnlyDebits_IsAFileWithNoCredits_NotAnError()
    {
        var statement = BankStatementParser.Parse(Utf8("Data;Descrizione;Importo\n03/10/2026;POS;-10,00\n"));

        Assert.Equal(1, statement.MovementCount);
        Assert.Empty(statement.Credits);
    }

    [Fact]
    public void Csv_TitlesOnly_IsEmpty()
    {
        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8("Data;Descrizione;Importo\n")));

        Assert.Equal(BankStatementErrorCodes.Empty, ex.Code);
    }

    [Fact]
    public void Csv_NoBytesAtAll_IsEmpty()
    {
        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse([]));

        Assert.Equal(BankStatementErrorCodes.Empty, ex.Code);
    }

    [Fact]
    public void Csv_MoreCreditsThanTheLimit_IsRefused()
    {
        var builder = new StringBuilder("Data;Descrizione;Importo\n");
        for (var i = 0; i < BankStatementLimits.MaxCredits + 1; i++)
            builder.Append("03/10/2026;Canone ").Append(i).Append(";900,00\n");

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(builder.ToString())));

        Assert.Equal(BankStatementErrorCodes.TooManyRows, ex.Code);
        Assert.Equal(new object[] { BankStatementLimits.MaxCredits }, ex.MessageArgs);
    }

    [Fact]
    public void Csv_ColumnsTheImporterDoesNotKnow_AreAskedToTheLandlord_WhoThenSaysWhichIsWhich()
    {
        const string csv = "Foo;Bar;Baz\n03/10/2026;Canone ottobre Verdi;900,00\n04/10/2026;Spesa;-20,00\n";

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(csv)));

        Assert.Equal(BankStatementErrorCodes.ColumnsUnrecognized, ex.Code);
        Assert.Equal(0, ex.Extensions["headerRow"]);
        var rows = Assert.IsType<string[][]>(ex.Extensions["rows"]);
        Assert.Equal(new[] { "Foo", "Bar", "Baz" }, rows[0]);
        Assert.Equal("Canone ottobre Verdi", rows[1][1]);

        var statement = BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: 0, Date: 0, Description: 1, Amount: 2));

        var credit = Assert.Single(statement.Credits);
        Assert.Equal(900.00m, credit.Amount);
        Assert.Equal("Canone ottobre Verdi", credit.Description);
    }

    [Fact]
    public void Csv_ALongPreambleBeforeUnknownTitles_DoesNotHideTheTitlesNorTheFirstMovementFromTheLandlord()
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= 7; i++)
            builder.Append("Informazione ").Append(i).Append(";valore\n");
        builder.Append("Foo;Bar;Baz\n03/10/2026;Canone ottobre Verdi;900,00\n04/10/2026;Spesa;-20,00\n");
        var csv = builder.ToString();

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(csv)));

        Assert.Equal(BankStatementErrorCodes.ColumnsUnrecognized, ex.Code);
        Assert.Equal(7, ex.Extensions["headerRow"]);
        var rows = Assert.IsType<string[][]>(ex.Extensions["rows"]);
        Assert.Equal(new[] { "Foo", "Bar", "Baz" }, rows[7]);
        Assert.Equal("Canone ottobre Verdi", rows[8][1]);
        Assert.Equal(3, ex.Extensions["columnCount"]);

        var statement = BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: 7, Date: 0, Description: 1, Amount: 2));

        Assert.Equal(900.00m, Assert.Single(statement.Credits).Amount);
    }

    [Fact]
    public void Csv_AFileWithNoTitlesAtAll_IsReadWithAMappingThatSaysSo()
    {
        const string csv = "03/10/2026;Canone ottobre Verdi;900,00\n04/10/2026;Spesa;-20,00\n";

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(csv)));
        Assert.Equal(BankStatementErrorCodes.ColumnsUnrecognized, ex.Code);
        Assert.Equal(-1, ex.Extensions["headerRow"]);

        var statement = BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: -1, Date: 0, Description: 1, Amount: 2));

        Assert.Equal(2, statement.MovementCount);
        Assert.Single(statement.Credits);
    }

    [Fact]
    public void Csv_AMappingWithAColumnTheFileDoesNotHave_IsRefused()
    {
        const string csv = "Foo;Bar;Baz\n03/10/2026;Canone;900,00\n";

        var ex = Assert.Throws<DomainRuleException>(() =>
            BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: 0, Date: 0, Description: 1, Amount: 7)));

        Assert.Equal(BankStatementErrorCodes.MappingInvalid, ex.Code);
    }

    [Fact]
    public void Csv_AMappingWithoutAnAmount_IsRefused()
    {
        const string csv = "Foo;Bar;Baz\n03/10/2026;Canone;900,00\n";

        var ex = Assert.Throws<DomainRuleException>(() =>
            BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: 0, Date: 0, Description: 1)));

        Assert.Equal(BankStatementErrorCodes.MappingInvalid, ex.Code);
    }

    [Fact]
    public void Csv_AMappingThatTakesTheMoneyInFromACreditColumn_NeedsNoAmountColumn()
    {
        const string csv = "Foo;Bar;In;Out\n03/10/2026;Canone;900,00;\n04/10/2026;Spesa;;20,00\n";

        var statement = BankStatementParser.Parse(Utf8(csv), new BankColumnMapping(HeaderRow: 0, Date: 0, Description: 1, Credit: 2, Debit: 3));

        Assert.Equal(900.00m, Assert.Single(statement.Credits).Amount);
        Assert.Equal(2, statement.MovementCount);
    }

    // ---------------------------------------------------------------- what is not a statement

    [Fact]
    public void APdf_IsNotAStatementTheImporterReads()
    {
        var ex = Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(Utf8("%PDF-1.7\nbinary")));

        Assert.Equal(BankStatementErrorCodes.FormatUnsupported, ex.Code);
    }

    [Fact]
    public void ALegacyExcelFile_IsNotRead_AndTheLandlordSavesItAsXlsx()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];

        var ex = Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(ole));

        Assert.Equal(BankStatementErrorCodes.FormatUnsupported, ex.Code);
    }

    [Fact]
    public void ABinaryFile_IsNotAStatement_WhateverItsName()
    {
        var bytes = Enumerable.Range(0, 600).Select(i => (byte)(i % 7 == 0 ? 0 : 200 + (i % 50))).ToArray();

        Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(bytes));
    }

    [Fact]
    public void AnXmlThatIsNotCamt_IsNotRead()
    {
        Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(Utf8("<html><body><table/></body></html>")));
    }

    [Fact]
    public void AFileOverTheLimit_IsRefusedBeforeItIsRead()
    {
        var ex = Assert.Throws<DomainPayloadTooLargeException>(() => BankStatementParser.Parse(new byte[BankStatementLimits.MaxFileBytes + 1]));

        Assert.Equal(BankStatementErrorCodes.TooLarge, ex.Code);
        Assert.Equal(new object[] { 8 }, ex.MessageArgs);
    }

    // ---------------------------------------------------------------- CAMT.053

    private const string CamtV2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt>
            <GrpHdr><MsgId>M1</MsgId><CreDtTm>2026-11-01T08:00:00</CreDtTm></GrpHdr>
            <Stmt>
              <Id>S1</Id>
              <Acct><Id><IBAN>IT60X0542811101000000123456</IBAN></Id></Acct>
              <Ntry>
                <NtryRef>N1</NtryRef>
                <Amt Ccy="EUR">900.00</Amt>
                <CdtDbtInd>CRDT</CdtDbtInd>
                <Sts>BOOK</Sts>
                <BookgDt><Dt>2026-10-03</Dt></BookgDt>
                <ValDt><Dt>2026-10-04</Dt></ValDt>
                <AcctSvcrRef>ASR1</AcctSvcrRef>
                <NtryDtls>
                  <TxDtls>
                    <Refs><EndToEndId>NOTPROVIDED</EndToEndId></Refs>
                    <RltdPties>
                      <Dbtr><Nm>Giulia Verdi</Nm></Dbtr>
                      <DbtrAcct><Id><IBAN>DE89370400440532013000</IBAN></Id></DbtrAcct>
                    </RltdPties>
                    <RmtInf><Ustrd>Canone ottobre 2026</Ustrd></RmtInf>
                  </TxDtls>
                </NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="EUR">45.50</Amt>
                <CdtDbtInd>DBIT</CdtDbtInd>
                <Sts>BOOK</Sts>
                <BookgDt><Dt>2026-10-05</Dt></BookgDt>
                <ValDt><Dt>2026-10-05</Dt></ValDt>
              </Ntry>
              <Ntry>
                <Amt Ccy="EUR">100.00</Amt>
                <CdtDbtInd>CRDT</CdtDbtInd>
                <Sts>PDNG</Sts>
                <BookgDt><Dt>2026-10-06</Dt></BookgDt>
                <ValDt><Dt>2026-10-06</Dt></ValDt>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void Camt053_ABookedCredit_IsReadWithItsPayerAndReferences()
    {
        var statement = BankStatementParser.Parse(Utf8(CamtV2));

        Assert.Equal(BankStatementFormat.Camt053, statement.Format);
        Assert.Equal(2, statement.MovementCount); // the pending entry is not money in the account yet
        var credit = Assert.Single(statement.Credits);
        Assert.Equal(new DateOnly(2026, 10, 3), credit.BookingDate);
        Assert.Equal(new DateOnly(2026, 10, 4), credit.ValueDate);
        Assert.Equal(900.00m, credit.Amount);
        Assert.Equal("EUR", credit.Currency);
        Assert.Equal("Giulia Verdi", credit.CounterpartyName);
        Assert.Equal(PayerIban, credit.CounterpartyIban);
        Assert.Equal("ASR1", credit.BankReference);
        Assert.Contains("Canone ottobre 2026", credit.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Camt053_TheNewerVersion_WithPartyAndCodedStatus_IsReadToo()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">
              <BkToCstmrStmt><Stmt>
                <Ntry>
                  <Amt Ccy="EUR">750.00</Amt>
                  <CdtDbtInd>CRDT</CdtDbtInd>
                  <Sts><Cd>BOOK</Cd></Sts>
                  <BookgDt><DtTm>2026-10-03T10:15:00+02:00</DtTm></BookgDt>
                  <ValDt><Dt>2026-10-03</Dt></ValDt>
                  <NtryDtls><TxDtls>
                    <RltdPties><Dbtr><Pty><Nm>Marco Bianchi</Nm></Pty></Dbtr></RltdPties>
                    <RmtInf><Ustrd>Affitto</Ustrd><Ustrd>ottobre</Ustrd></RmtInf>
                  </TxDtls></NtryDtls>
                </Ntry>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;

        var credit = Assert.Single(BankStatementParser.Parse(Utf8(xml)).Credits);

        Assert.Equal(750.00m, credit.Amount);
        Assert.Equal(new DateOnly(2026, 10, 3), credit.BookingDate);
        Assert.Equal("Marco Bianchi", credit.CounterpartyName);
        Assert.Equal("Affitto ottobre", credit.Description);
    }

    [Fact]
    public void Camt053_ABatchEntry_IsOneCreditForEachLineWithItsOwnAmount()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt><Stmt>
                <Ntry>
                  <Amt Ccy="EUR">900.00</Amt>
                  <CdtDbtInd>CRDT</CdtDbtInd>
                  <BookgDt><Dt>2026-10-03</Dt></BookgDt>
                  <ValDt><Dt>2026-10-03</Dt></ValDt>
                  <NtryDtls>
                    <TxDtls>
                      <AmtDtls><TxAmt><Amt Ccy="EUR">500.00</Amt></TxAmt></AmtDtls>
                      <RltdPties><Dbtr><Nm>Giulia Verdi</Nm></Dbtr></RltdPties>
                      <RmtInf><Ustrd>Canone</Ustrd></RmtInf>
                    </TxDtls>
                    <TxDtls>
                      <AmtDtls><TxAmt><Amt Ccy="EUR">400.00</Amt></TxAmt></AmtDtls>
                      <RltdPties><Dbtr><Nm>Anna Rossi</Nm></Dbtr></RltdPties>
                      <RmtInf><Ustrd>Spese</Ustrd></RmtInf>
                    </TxDtls>
                  </NtryDtls>
                </Ntry>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;

        var credits = BankStatementParser.Parse(Utf8(xml)).Credits;

        Assert.Equal(new[] { 500.00m, 400.00m }, credits.Select(c => c.Amount).ToArray());
        Assert.Equal(new[] { "Giulia Verdi", "Anna Rossi" }, credits.Select(c => c.CounterpartyName).ToArray());
    }

    [Theory]
    [InlineData("DBIT")] // the bank takes back a credit it had booked (a returned transfer): money out
    [InlineData("CRDT")] // the bank gives back a debit (an outgoing payment that comes back): money in, but not a tenant paying
    public void Camt053_AReversal_IsNeverOfferedAsACredit(string indicator)
    {
        var xml = $"""
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt><Stmt>
                <Ntry>
                  <Amt Ccy="EUR">900.00</Amt>
                  <CdtDbtInd>{indicator}</CdtDbtInd>
                  <RvslInd>true</RvslInd>
                  <BookgDt><Dt>2026-10-03</Dt></BookgDt>
                  <ValDt><Dt>2026-10-03</Dt></ValDt>
                  <NtryDtls><TxDtls><RltdPties><Dbtr><Nm>Giulia Verdi</Nm></Dbtr></RltdPties></TxDtls></NtryDtls>
                </Ntry>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;

        var statement = BankStatementParser.Parse(Utf8(xml));

        Assert.Equal(1, statement.MovementCount);
        Assert.Empty(statement.Credits);
    }

    [Fact]
    public void Camt053_ACreditWithTheReversalIndicatorFalse_IsAnOrdinaryCredit()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt><Stmt>
                <Ntry>
                  <Amt Ccy="EUR">900.00</Amt>
                  <CdtDbtInd>CRDT</CdtDbtInd>
                  <RvslInd>false</RvslInd>
                  <BookgDt><Dt>2026-10-03</Dt></BookgDt>
                  <ValDt><Dt>2026-10-03</Dt></ValDt>
                </Ntry>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;

        Assert.Single(BankStatementParser.Parse(Utf8(xml)).Credits);
    }

    [Fact]
    public void Camt053_TheInformationOfAWholeEntry_GoesWithEachLineOfIt_CutToTheSizeOfADescription()
    {
        var entryInformation = new string('x', 5_000);
        var xml = $"""
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt><Stmt>
                <Ntry>
                  <Amt Ccy="EUR">900.00</Amt>
                  <CdtDbtInd>CRDT</CdtDbtInd>
                  <BookgDt><Dt>2026-10-03</Dt></BookgDt>
                  <ValDt><Dt>2026-10-03</Dt></ValDt>
                  <AddtlNtryInf>Bonifico {entryInformation}</AddtlNtryInf>
                  <NtryDtls>
                    <TxDtls>
                      <AmtDtls><TxAmt><Amt Ccy="EUR">500.00</Amt></TxAmt></AmtDtls>
                      <RmtInf><Ustrd>Canone</Ustrd></RmtInf>
                    </TxDtls>
                    <TxDtls>
                      <AmtDtls><TxAmt><Amt Ccy="EUR">400.00</Amt></TxAmt></AmtDtls>
                      <RmtInf><Ustrd>Spese</Ustrd></RmtInf>
                    </TxDtls>
                  </NtryDtls>
                </Ntry>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;

        var credits = BankStatementParser.Parse(Utf8(xml)).Credits;

        Assert.Equal(2, credits.Count);
        Assert.All(credits, credit =>
        {
            Assert.StartsWith(credit.Amount == 500.00m ? "Canone Bonifico " : "Spese Bonifico ", credit.Description, StringComparison.Ordinal);
            Assert.Equal(BankStatementLimits.MaxTextLength, credit.Description.Length);
        });
    }

    [Fact]
    public void Camt053_ADocumentTypeWithAnExternalEntity_IsNeverFollowed()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Document [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02"><BkToCstmrStmt><Stmt>
              <Ntry><Amt Ccy="EUR">1.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><AddtlNtryInf>&secret;</AddtlNtryInf></Ntry>
            </Stmt></BkToCstmrStmt></Document>
            """;

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(xml)));

        Assert.Equal(BankStatementErrorCodes.Unreadable, ex.Code);
    }

    [Fact]
    public void Camt053_TruncatedXml_IsUnreadable()
    {
        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(CamtV2[..(CamtV2.Length / 2)])));

        Assert.Equal(BankStatementErrorCodes.Unreadable, ex.Code);
    }

    // ---------------------------------------------------------------- MT940

    private const string Mt940 = """
        :20:STATEMENT1
        :25:IT60X0542811101000000123456
        :28C:00001/001
        :60F:C261001EUR1000,00
        :61:2610031003C900,00NTRFNONREF//BANKREF1
        :86:?00BONIFICO?20CANONE OTTOBRE 2026?32VERDI GIULIA?33?60IBAN DE89370400440532013000
        :61:2610051005D45,50NTRFNONREF
        :86:/NAME/COFFEE SHOP/REMI/POS
        :61:2610071007C500,00NTRFNONREF//BANKREF2
        :86:/ORDP//NAME/ROSSI MARIO/REMI/AFFITTO SETTEMBRE
        :61:261008CR12,00NTRFNONREF
        :86:Interessi attivi
        :62F:C261031EUR1354,50
        -
        """;

    [Fact]
    public void Mt940_TheThreeWaysToWriteTheInformation_AreReadWithTheirPayers()
    {
        var statement = BankStatementParser.Parse(Utf8(Mt940.Replace("\r", string.Empty, StringComparison.Ordinal)));

        Assert.Equal(BankStatementFormat.Mt940, statement.Format);
        Assert.Equal(4, statement.MovementCount);
        Assert.Equal(3, statement.Credits.Count);

        var structured = statement.Credits[0];
        Assert.Equal(new DateOnly(2026, 10, 3), structured.ValueDate);
        Assert.Equal(new DateOnly(2026, 10, 3), structured.BookingDate);
        Assert.Equal(900.00m, structured.Amount);
        Assert.Equal("VERDI GIULIA", structured.CounterpartyName);
        Assert.Equal("BANKREF1", structured.BankReference);
        Assert.Equal(PayerIban, structured.CounterpartyIban);
        Assert.Contains("CANONE OTTOBRE 2026", structured.Description, StringComparison.Ordinal);

        var tagged = statement.Credits[1];
        Assert.Equal(500.00m, tagged.Amount);
        Assert.Equal("ROSSI MARIO", tagged.CounterpartyName);
        Assert.Equal("AFFITTO SETTEMBRE", tagged.Description);

        // A statement line without the entry date and with the funds code «R» after the mark, and information in free text.
        var plain = statement.Credits[2];
        Assert.Equal(12.00m, plain.Amount);
        Assert.Equal(new DateOnly(2026, 10, 8), plain.ValueDate);
        Assert.Equal("Interessi attivi", plain.Description);
        Assert.Null(plain.CounterpartyName);
    }

    [Theory]
    [InlineData("RD")] // the reversal of a debit: money back in the account, but a bank taking back its own entry and not a tenant paying
    [InlineData("RC")] // the reversal of a credit: money out
    public void Mt940_AReversal_IsCountedAndNeverOfferedAsACredit(string mark)
    {
        var text = $":20:X\n:61:2610031003{mark}50,00NTRFNONREF\n:86:Storno\n";

        var statement = BankStatementParser.Parse(Utf8(text));

        Assert.Equal(1, statement.MovementCount);
        Assert.Empty(statement.Credits);
    }

    [Fact]
    public void Mt940_TheCurrencyIsTheOneOfTheOpeningBalance_NotAlwaysEuro()
    {
        const string text = ":20:X\n:25:ACC\n:60F:C261001GBP1000,00\n:61:2610031003C800,00NTRFNONREF\n:86:Rent\n:62F:C261031GBP1800,00\n";

        var credit = Assert.Single(BankStatementParser.Parse(Utf8(text)).Credits);

        Assert.Equal("GBP", credit.Currency);
        Assert.Equal(800.00m, credit.Amount);
    }

    [Fact]
    public void Mt940_WithNoOpeningBalance_IsInEuro()
    {
        const string text = ":20:X\n:61:2610031003C800,00NTRFNONREF\n:86:Canone\n";

        Assert.Equal("EUR", Assert.Single(BankStatementParser.Parse(Utf8(text)).Credits).Currency);
    }

    [Fact]
    public void Mt940_AnInformationFieldOfMillionsOfSubfieldMarks_IsCutBeforeItIsSearched()
    {
        var text = ":20:X\n:61:2610031003C800,00NTRFNONREF\n:86:" + string.Concat(Enumerable.Repeat("?20ab", 200_000)) + "\n";

        var credit = Assert.Single(BankStatementParser.Parse(Utf8(text)).Credits);

        Assert.True(credit.Description.Length <= BankStatementLimits.MaxTextLength);
    }

    [Fact]
    public void Mt940_MoreLinesThanTheLimit_IsRefusedBeforeItFillsTheMemory()
    {
        var builder = new StringBuilder(":20:X\n");
        for (var i = 0; i < BankStatementLimits.MaxRows + 5; i++)
            builder.Append(":61:2610031003C1,00NTRFNONREF\n:86:x\n");

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(Utf8(builder.ToString())));

        Assert.Equal(BankStatementErrorCodes.TooManyRows, ex.Code);
    }

    [Fact]
    public void Mt940_AEntryDateAcrossNewYear_BelongsToTheRightYear()
    {
        const string text = ":20:X\n:61:2612300102C50,00NTRFNONREF\n:86:Canone\n";

        var credit = Assert.Single(BankStatementParser.Parse(Utf8(text)).Credits);

        Assert.Equal(new DateOnly(2026, 12, 30), credit.ValueDate);
        Assert.Equal(new DateOnly(2027, 1, 2), credit.BookingDate);
    }
}
