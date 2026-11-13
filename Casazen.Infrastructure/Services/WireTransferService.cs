using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Wire transfer service (ADR-017): generates SEPA pain.001 (Italy) or Swiss Payment Standards (Switzerland) XML files
/// for paying property owners. One wire per owner, one file per currency and debit account.
/// </summary>
/// <remarks>
/// <para><b>The file is validated before anything is saved</b> (E06/F05/US01/T01, NF-25): against the official schema of its currency (EUR: SEPA, CHF: Swiss;
/// <see cref="Pain001SchemaProvider"/>, <see cref="XsdValidator"/>: no DTD, no resolver, no network) and against what a schema cannot say, the sums
/// (<see cref="Pain001Checks"/>). The batch, its statements and the file are written by one <c>SaveChanges</c> after that: a file that does not validate
/// leaves no row behind (422 <c>wire_batch_xml_invalid</c> or <c>wire_batch_content_invalid</c>), and a build without the official schema of the currency
/// produces no file at all (422 <c>wire_batch_schema_unavailable</c>), never a file that was not validated.</para>
/// <para><b>Privacy.</b> The IBAN and the name of an owner are written in the file and in no log line, message or exception. The errors of the validation are
/// the line and position of the schema errors and the names of the ISO elements whose rule is broken.</para>
/// </remarks>
public sealed class WireTransferService(
    AppDbContext db,
    ILogger<WireTransferService> logger,
    Func<string, XmlSchemaSet>? schemaSource = null) : IWireTransferService
{
    // ISO 20022 pain.001.001.03 namespace (SEPA and Swiss both use this)
    private const string PainNamespace = Pain001SchemaProvider.Namespace;

    // The schemas a file is validated against, by currency: the official ones of the build (422 when they are not installed). Only a test supplies its own.
    private readonly Func<string, XmlSchemaSet> schemaFor = schemaSource ?? Pain001SchemaProvider.GetSchemas;

    /// <inheritdoc />
    public async Task<(WireBatch Batch, string XmlContent)> CreateBatchAsync(
        Guid orgId,
        List<Guid> statementIds,
        string debitAccount,
        CancellationToken cancellationToken = default)
    {
        if (statementIds.Count == 0)
        {
            throw new DomainRuleException("no_statements_provided", "NoStatementsProvided");
        }

        // Validate and normalize the debit account IBAN
        var normalizedDebitAccount = NormalizeIban(debitAccount);
        if (!ValidateIban(normalizedDebitAccount))
        {
            throw new DomainRuleException("invalid_debit_account_iban", "InvalidDebitAccountIban");
        }

        // Load statements with owner and ownership details
        var statements = await db.OwnerStatements
            .Include(s => s.PropertyOwner)
            .Where(s => statementIds.Contains(s.Id) && s.OrgId == orgId)
            .ToListAsync(cancellationToken);

        if (statements.Count != statementIds.Count)
        {
            throw new DomainRuleException("statements_not_found_or_not_owned", "StatementsNotFoundOrNotOwned");
        }

        // Ensure all statements are Published
        if (statements.Any(s => s.Status != StatementStatus.Published))
        {
            throw new DomainRuleException("statements_not_published", "StatementsNotPublished");
        }

        // Ensure all statements are in the same currency (a letter case or a space does not make another one)
        var currencies = statements.Select(s => NormalizeCurrency(s.Currency)).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
        {
            throw new DomainRuleException("mixed_currencies_not_allowed", "MixedCurrenciesNotAllowed");
        }

        // Only the currencies of a wire file: EUR (the SEPA file) and CHF (the Swiss one). Any other currency in a SEPA file is not a payment a bank takes.
        var currency = currencies[0];
        if (currency is not (WireTransferErrors.SepaCurrency or WireTransferErrors.SwissCurrency))
        {
            throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported");
        }

        // Check if any statement is already in a batch (AC-NF19-8)
        var statementsInBatches = await db.Set<WireBatchStatement>()
            .Where(wbs => statementIds.Contains(wbs.OwnerStatementId))
            .Select(wbs => wbs.OwnerStatementId)
            .ToListAsync(cancellationToken);

        if (statementsInBatches.Any())
        {
            throw new DomainRuleException("statements_already_in_batch", "StatementsAlreadyInBatch");
        }

        // Group statements by PropertyOwnerId to create one wire per owner
        var ownerGroups = statements
            .GroupBy(s => s.PropertyOwnerId)
            .Select(g => new
            {
                PropertyOwnerId = g.Key,
                Owner = g.First().PropertyOwner,
                Statements = g.ToList(),
                TotalAmount = g.Sum(s => s.NetPayable)
            })
            .ToList();

        // Validate all owner IBANs
        var transfers = new List<(PropertyOwner Owner, decimal Amount, string Iban)>();
        foreach (var group in ownerGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Owner.Iban))
            {
                // No name of the owner in the exception: it is not shown, and it would travel in the arguments of every log of the error.
                throw new DomainRuleException("owner_missing_iban", "OwnerMissingIban");
            }

            var normalizedIban = NormalizeIban(group.Owner.Iban);
            if (!ValidateIban(normalizedIban))
            {
                // NEVER log the IBAN itself (ADR-012-R2)
                logger.LogWarning(
                    "Owner {OwnerId} has invalid IBAN (validation failed)",
                    group.Owner.Id);
                throw new DomainRuleException("owner_invalid_iban", "OwnerInvalidIban");
            }

            // A transfer is a positive amount with at most two decimals: never zero, never negative, never rounded in silence (the statements are
            // rounded once at their total, ADR-017, so the sum of an owner's statements already has two).
            if (group.TotalAmount <= 0m || decimal.Round(group.TotalAmount, 2) != group.TotalAmount)
            {
                throw new DomainRuleException(WireTransferErrors.AmountInvalid, "WireAmountInvalid");
            }

            transfers.Add((group.Owner, group.TotalAmount, normalizedIban));
        }

        // The batch is built in memory: nothing is added to the context until its file has been validated.
        var batch = new WireBatch
        {
            OrgId = orgId,
            Currency = currency,
            DebitAccount = normalizedDebitAccount,
            TotalAmount = transfers.Sum(t => t.Amount),
            TransferCount = transfers.Count,
            Status = WireBatchStatus.Generated,
            CreatedAt = DateTime.UtcNow
        };

        // Generate XML based on currency: EUR is the SEPA file, CHF the Swiss one
        var xmlContent = currency == WireTransferErrors.SwissCurrency
            ? GenerateSwissPaymentXml(batch, transfers, normalizedDebitAccount)
            : GenerateSepaXml(batch, transfers, normalizedDebitAccount);

        // Validate before anything is saved: a file that does not validate leaves no batch behind, and with no schema there is no file.
        ValidateFile(xmlContent, batch);

        // One save for the batch, the statements it covers (AC-NF19-8) and the file it was validated for: all or nothing. A child with its own
        // Guid goes through its set (added to the collection alone it would be tracked as Modified).
        batch.XmlContent = xmlContent;
        db.WireBatches.Add(batch);
        foreach (var statementId in statementIds)
        {
            db.Set<WireBatchStatement>().Add(new WireBatchStatement
            {
                OrgId = orgId,
                WireBatchId = batch.Id,
                OwnerStatementId = statementId,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created wire batch {BatchId} with {TransferCount} transfers for {TotalAmount:N2} {Currency}",
            batch.Id,
            batch.TransferCount,
            batch.TotalAmount,
            batch.Currency);

        return (batch, xmlContent);
    }

    /// <inheritdoc />
    public async Task ConfirmDownloadAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await db.WireBatches.FindAsync([batchId], cancellationToken);
        if (batch is null)
        {
            throw new NotFoundException($"Wire batch {batchId} not found");
        }

        if (batch.Status != WireBatchStatus.Generated)
        {
            throw new DomainRuleException("batch_already_downloaded_or_paid", "BatchAlreadyDownloadedOrPaid");
        }

        batch.Status = WireBatchStatus.Downloaded;
        batch.DownloadedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ConfirmPaidAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await db.WireBatches.FindAsync([batchId], cancellationToken);
        if (batch is null)
        {
            throw new NotFoundException($"Wire batch {batchId} not found");
        }

        if (batch.Status == WireBatchStatus.Generated)
        {
            throw new DomainRuleException("batch_not_downloaded", "BatchNotDownloaded");
        }

        if (batch.Status == WireBatchStatus.Paid)
        {
            throw new DomainRuleException("batch_already_paid", "BatchAlreadyPaid");
        }

        batch.Status = WireBatchStatus.Paid;
        batch.PaidAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(WireBatch Batch, string XmlContent)> GetBatchFileAsync(
        Guid orgId,
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await db.WireBatches
            .Where(wb => wb.Id == batchId && wb.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (batch is null)
        {
            throw new NotFoundException($"Wire batch {batchId} not found");
        }

        if (string.IsNullOrEmpty(batch.XmlContent))
        {
            throw new DomainRuleException("batch_xml_not_found", "BatchXmlNotFound");
        }

        // Mark as downloaded on first retrieval
        if (batch.Status == WireBatchStatus.Generated)
        {
            batch.Status = WireBatchStatus.Downloaded;
            batch.DownloadedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return (batch, batch.XmlContent);
    }

    /// <summary>
    /// Normalize an IBAN: remove spaces and convert to uppercase.
    /// </summary>
    private static string NormalizeIban(string iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
            return string.Empty;

        return iban.Replace(" ", "").Replace("-", "").ToUpperInvariant();
    }

    /// <summary>
    /// Validate an IBAN using mod-97 check digits (ISO 13616).
    /// </summary>
    private static bool ValidateIban(string iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
            return false;

        // IBAN must be 15-34 characters
        if (iban.Length < 15 || iban.Length > 34)
            return false;

        // Must start with two letters (country code) followed by two digits (check digits)
        if (!char.IsLetter(iban[0]) || !char.IsLetter(iban[1]))
            return false;

        if (!char.IsDigit(iban[2]) || !char.IsDigit(iban[3]))
            return false;

        // Move first 4 characters to end
        var rearranged = iban.Substring(4) + iban.Substring(0, 4);

        // Replace letters with numbers (A=10, B=11, ..., Z=35)
        var numeric = new StringBuilder();
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c))
            {
                numeric.Append(c);
            }
            else if (char.IsLetter(c))
            {
                numeric.Append((int)c - 55); // A=10, B=11, ..., Z=35
            }
            else
            {
                return false; // Invalid character
            }
        }

        // Compute mod 97
        var numericString = numeric.ToString();
        var remainder = 0;
        foreach (var digit in numericString)
        {
            remainder = (remainder * 10 + (digit - '0')) % 97;
        }

        return remainder == 1;
    }

    /// <summary>
    /// Generate SEPA pain.001.001.03 XML for EUR transfers (Italy).
    /// </summary>
    private string GenerateSepaXml(
        WireBatch batch,
        List<(PropertyOwner Owner, decimal Amount, string Iban)> transfers,
        string debitAccount)
    {
        var ns = XNamespace.Get(PainNamespace);
        var now = DateTime.UtcNow;
        var messageId = $"BATCH-{batch.Id:N}".Substring(0, 35);
        var paymentId = $"PMT-{batch.Id:N}".Substring(0, 35);

        // Build credit transfer transaction info elements
        var creditTransfers = transfers.Select((t, i) => new XElement(ns + "CdtTrfTxInf",
            new XElement(ns + "PmtId",
                new XElement(ns + "EndToEndId", $"TXN-{i + 1:D5}")),
            new XElement(ns + "Amt",
                new XElement(ns + "InstdAmt",
                    new XAttribute("Ccy", batch.Currency),
                    t.Amount.ToString("F2", CultureInfo.InvariantCulture))),
            new XElement(ns + "CdtrAgt",
                new XElement(ns + "FinInstnId",
                    new XElement(ns + "BIC", "NOTPROVIDED"))),
            new XElement(ns + "Cdtr",
                new XElement(ns + "Nm", $"{t.Owner.FirstName} {t.Owner.LastName}")),
            new XElement(ns + "CdtrAcct",
                new XElement(ns + "Id",
                    new XElement(ns + "IBAN", t.Iban))),
            new XElement(ns + "RmtInf",
                new XElement(ns + "Ustrd", "Owner statement payment"))));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "Document",
                new XElement(ns + "CstmrCdtTrfInitn",
                    new XElement(ns + "GrpHdr",
                        new XElement(ns + "MsgId", messageId),
                        new XElement(ns + "CreDtTm", now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(ns + "NbOfTxs", batch.TransferCount),
                        new XElement(ns + "CtrlSum", batch.TotalAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement(ns + "InitgPty",
                            new XElement(ns + "Nm", "CasaZen Agency"))),
                    new XElement(ns + "PmtInf",
                        new XElement(ns + "PmtInfId", paymentId),
                        new XElement(ns + "PmtMtd", "TRF"),
                        new XElement(ns + "BtchBookg", "true"),
                        new XElement(ns + "NbOfTxs", batch.TransferCount),
                        new XElement(ns + "CtrlSum", batch.TotalAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement(ns + "PmtTpInf",
                            new XElement(ns + "SvcLvl",
                                new XElement(ns + "Cd", "SEPA"))),
                        new XElement(ns + "ReqdExctnDt", now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement(ns + "Dbtr",
                            new XElement(ns + "Nm", "CasaZen Agency")),
                        new XElement(ns + "DbtrAcct",
                            new XElement(ns + "Id",
                                new XElement(ns + "IBAN", debitAccount))),
                        new XElement(ns + "DbtrAgt",
                            new XElement(ns + "FinInstnId",
                                new XElement(ns + "BIC", "NOTPROVIDED"))),
                        creditTransfers))));

        return FormatXml(doc);
    }

    /// <summary>
    /// Generate Swiss Payment Standards (pain.001.001.03) XML for CHF transfers (Switzerland).
    /// </summary>
    private string GenerateSwissPaymentXml(
        WireBatch batch,
        List<(PropertyOwner Owner, decimal Amount, string Iban)> transfers,
        string debitAccount)
    {
        var ns = XNamespace.Get(PainNamespace);
        var now = DateTime.UtcNow;
        var messageId = $"BATCH-{batch.Id:N}".Substring(0, 35);
        var paymentId = $"PMT-{batch.Id:N}".Substring(0, 35);

        // Build credit transfer transaction info elements
        var creditTransfers = transfers.Select((t, i) => new XElement(ns + "CdtTrfTxInf",
            new XElement(ns + "PmtId",
                new XElement(ns + "EndToEndId", $"TXN-{i + 1:D5}")),
            new XElement(ns + "Amt",
                new XElement(ns + "InstdAmt",
                    new XAttribute("Ccy", batch.Currency),
                    t.Amount.ToString("F2", CultureInfo.InvariantCulture))),
            new XElement(ns + "CdtrAgt",
                new XElement(ns + "FinInstnId",
                    new XElement(ns + "BIC", "NOTPROVIDED"))),
            new XElement(ns + "Cdtr",
                new XElement(ns + "Nm", $"{t.Owner.FirstName} {t.Owner.LastName}")),
            new XElement(ns + "CdtrAcct",
                new XElement(ns + "Id",
                    new XElement(ns + "IBAN", t.Iban))),
            new XElement(ns + "RmtInf",
                new XElement(ns + "Ustrd", "Owner statement payment"))));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "Document",
                new XElement(ns + "CstmrCdtTrfInitn",
                    new XElement(ns + "GrpHdr",
                        new XElement(ns + "MsgId", messageId),
                        new XElement(ns + "CreDtTm", now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(ns + "NbOfTxs", batch.TransferCount),
                        new XElement(ns + "CtrlSum", batch.TotalAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement(ns + "InitgPty",
                            new XElement(ns + "Nm", "CasaZen Agency"))),
                    new XElement(ns + "PmtInf",
                        new XElement(ns + "PmtInfId", paymentId),
                        new XElement(ns + "PmtMtd", "TRF"),
                        new XElement(ns + "BtchBookg", "true"),
                        new XElement(ns + "NbOfTxs", batch.TransferCount),
                        new XElement(ns + "CtrlSum", batch.TotalAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement(ns + "PmtTpInf",
                            new XElement(ns + "SvcLvl",
                                new XElement(ns + "Cd", "URGP"))), // Swiss uses URGP instead of SEPA
                        new XElement(ns + "ReqdExctnDt", now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement(ns + "Dbtr",
                            new XElement(ns + "Nm", "CasaZen Agency")),
                        new XElement(ns + "DbtrAcct",
                            new XElement(ns + "Id",
                                new XElement(ns + "IBAN", debitAccount))),
                        new XElement(ns + "DbtrAgt",
                            new XElement(ns + "FinInstnId",
                                new XElement(ns + "BIC", "NOTPROVIDED"))),
                        creditTransfers))));

        return FormatXml(doc);
    }

    /// <summary>
    /// Format an XDocument as indented XML string.
    /// </summary>
    private static string FormatXml(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using (var xmlWriter = XmlWriter.Create(stream, settings))
        {
            doc.Save(xmlWriter);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Validate the file of a batch before the batch is saved (E06/F05/US01/T01, NF-25): against the official schema of its currency (EUR: SEPA,
    /// CHF: Swiss; no DTD, no resolver, no network), then against what a schema cannot say, the sums. 422 <c>wire_batch_schema_unavailable</c> when the
    /// official schema of the currency is not installed (the file is never produced without its validation), <c>wire_batch_xml_invalid</c> with the line and
    /// position of the first errors, <c>wire_batch_content_invalid</c> with the names of the broken rules. Never a value of the file: no IBAN, no name,
    /// neither in the answer nor in the log (the text of a schema error can quote one, so it is not used).
    /// </summary>
    private void ValidateFile(string xmlContent, WireBatch batch)
    {
        var schemas = schemaFor(batch.Currency);

        var errors = XsdValidator.Validate(xmlContent, schemas);
        if (errors.Count > 0)
        {
            var positions = string.Join(", ", errors.Take(WireTransferErrors.ListedErrors).Select(e => $"{e.Line}:{e.Position}"));
            logger.LogWarning(
                "Wire file of {TransferCount} transfers in {Currency} does not validate against the official schema: {ErrorCount} errors",
                batch.TransferCount,
                batch.Currency,
                errors.Count);
            throw new DomainRuleException(WireTransferErrors.XmlInvalid, "WireBatchXmlInvalid", errors.Count, positions);
        }

        var violations = Pain001Checks.Violations(xmlContent, batch.Currency, batch.TotalAmount, batch.TransferCount);
        if (violations.Count > 0)
        {
            var rules = string.Join(", ", violations);
            logger.LogWarning(
                "Wire file of {TransferCount} transfers in {Currency} does not match its lines: {Rules}",
                batch.TransferCount,
                batch.Currency,
                rules);
            throw new DomainRuleException(WireTransferErrors.ContentInvalid, "WireBatchContentInvalid", rules);
        }
    }

    /// <summary>The currency code of a statement: trimmed and in upper case (the stored one is a free text of three letters).</summary>
    private static string NormalizeCurrency(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant();
}
