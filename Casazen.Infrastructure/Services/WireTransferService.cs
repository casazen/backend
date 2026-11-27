using System.Globalization;
using System.Xml.Schema;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Wire transfer service (ADR-017, E16/F01/US01/T01, E16/F01/US02/T01): builds the bank file that pays the owners from their <b>confirmed payouts</b>, the net after
/// the withholding (<see cref="OwnerPayout"/>), and never from a statement. CHF is the Swiss <c>pain.001.001.09.ch.03</c> file, EUR the ISO 20022
/// <c>pain.001.001.09</c> file with the rules of the SEPA credit transfer of the EPC (both written by <see cref="Pain001V09Writer"/>). One transfer per payout,
/// one file per currency; the debtor and the debit account are the issuer profile of the org. A payout is paid by one batch only.
/// </summary>
/// <remarks>
/// <para><b>The file is validated before anything is saved</b> (E06/F05/US01/T01, NF-25, NF-113): against the official schema of its currency (EUR: ISO, CHF: Swiss;
/// <see cref="Pain001SchemaProvider"/>, <see cref="XsdValidator"/>: no DTD, no resolver, no network) and against what a schema cannot say, the sums and the SEPA
/// rules of the EUR file (<see cref="Pain001Checks"/>). The batch, its payouts and the file are written by one <c>SaveChanges</c> after that: a file that does not
/// validate leaves no row behind (422 <c>wire_batch_xml_invalid</c> or <c>wire_batch_content_invalid</c>), and a build without the official schema of the currency
/// produces no file at all (422 <c>wire_batch_schema_unavailable</c>), never a file that was not validated.</para>
/// <para><b>One batch for a payout.</b> The batch is made under the lock of the owner accounting of the org, the one a payout confirmation and a reversal take, so a
/// payout reversed while its batch is made is seen by one of them; the unique index of <see cref="WireBatchPayout"/> is the backstop of two requests that could not
/// be told apart.</para>
/// <para><b>Privacy.</b> The IBAN, the name and the address of an owner are written in the file and in no log line, message or exception: the refusals about owners
/// list their <b>ids</b>. The errors of the validation are the line and position of the schema errors and the names of the ISO elements whose rule is broken. The file
/// and the debit account are encrypted at rest (<c>EncryptedColumns</c>).</para>
/// <para><b>The reads of the wire page</b> (<see cref="ListPayablePayoutsAsync"/>, <see cref="GetBatchPayoutsAsync"/>) show which payouts can still be paid and which ones a
/// batch pays: the owner by id and name, the period, the net amount and whether the owner has an IBAN, as a yes or a no. They read the IBAN of an owner only to tell whether it
/// is there and give it to nobody; the file and the debit account of a batch are never read by them.</para>
/// <para><b>Tenant.</b> Every read and write filters on the org of the caller explicitly, besides the global filter of the context (a <c>Find</c> by key does not
/// apply it): a batch or a payout of another org does not exist for the caller.</para>
/// </remarks>
public sealed class WireTransferService(
    AppDbContext db,
    ILogger<WireTransferService> logger,
    Func<string, XmlSchemaSet>? schemaSource = null,
    TimeProvider? time = null) : IWireTransferService
{
    // The schemas a file is validated against, by currency: the official ones of the build (422 when they are not installed). Only a test supplies its own.
    private readonly Func<string, XmlSchemaSet> schemaFor = schemaSource ?? Pain001SchemaProvider.GetSchemas;

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<(WireBatch Batch, string XmlContent)> CreateBatchAsync(
        Guid orgId,
        IReadOnlyList<Guid> payoutIds,
        DateOnly executionDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payoutIds);

        // An empty list is never «all the payouts», and a request has a size.
        var ids = payoutIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new DomainRuleException(WireTransferErrors.NoPayoutsProvided, "NoPayoutsProvided");
        }

        if (ids.Count > WireTransferErrors.MaxTransfers)
        {
            throw new DomainRuleException(WireTransferErrors.BatchTooLarge, "WireBatchTooLarge", WireTransferErrors.MaxTransfers);
        }

        // The bank is asked for today (Europe/Rome) or a later day, within a year.
        var today = clock.TodayInRomeAsDateOnly();
        if (executionDate < today || executionDate > today.AddYears(1))
        {
            throw new DomainRuleException(WireTransferErrors.ExecutionDateInvalid, "WireExecutionDateInvalid");
        }

        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));

        // The payouts: all in the org, all confirmed, all of a version published to the owner, all in one currency of a wire file, none in a batch.
        var payouts = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
            .ToListAsync(cancellationToken);
        if (payouts.Count != ids.Count)
        {
            throw new DomainRuleException(WireTransferErrors.PayoutsNotFound, "PayoutsNotFoundOrNotOwned");
        }

        if (payouts.Any(p => p.Status != OwnerPayoutStatus.Confirmed))
        {
            throw new DomainRuleException(WireTransferErrors.PayoutsNotConfirmed, "WirePayoutsNotConfirmed");
        }

        var versionIds = payouts.Select(p => p.OwnerStatementVersionId).Distinct().ToList();
        var versions = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && versionIds.Contains(v.Id) && v.PublishedAt != null)
            .Select(v => new { v.Id, v.PeriodYear, v.PeriodMonth })
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        if (versions.Count != versionIds.Count)
        {
            throw new DomainRuleException(WireTransferErrors.PayoutsNotPublished, "WirePayoutsNotPublished");
        }

        // One currency per batch (a letter case or a space does not make another one), and only the currencies of a wire file.
        var currencies = payouts.Select(p => NormalizeCurrency(p.Currency)).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
        {
            throw new DomainRuleException(WireTransferErrors.MixedCurrencies, "MixedCurrenciesNotAllowed");
        }

        var currency = currencies[0];
        if (currency is not (WireTransferErrors.SepaCurrency or WireTransferErrors.SwissCurrency))
        {
            throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported");
        }

        // A transfer is a positive amount with at most two decimals: the net of the payout, never zero, never negative, never rounded in silence.
        if (payouts.Any(p => p.NetAmount <= 0m || decimal.Round(p.NetAmount, 2) != p.NetAmount))
        {
            throw new DomainRuleException(WireTransferErrors.AmountInvalid, "WireAmountInvalid");
        }

        if (await db.WireBatchPayouts.AsNoTracking().AnyAsync(w => w.OrgId == orgId && ids.Contains(w.OwnerPayoutId), cancellationToken))
        {
            throw new DomainRuleException(WireTransferErrors.PayoutsAlreadyInBatch, "PayoutsAlreadyInBatch");
        }

        // The debtor and the debit account: the issuer profile of the org.
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(cancellationToken);
        var flavor = Pain001V09Writer.FlavorOf(currency);
        var debtor = new Pain001Party(profile?.LegalName, profile?.AddressStreet, profile?.AddressCivic, profile?.PostalCode, profile?.City, profile?.CountryCode);
        if (profile is null || !Pain001V09Writer.IsComplete(debtor, flavor))
        {
            throw new DomainRuleException(WireTransferErrors.DebtorIncomplete, "WireDebtorIncomplete");
        }

        if (string.IsNullOrWhiteSpace(profile.Iban))
        {
            throw new DomainRuleException(WireTransferErrors.DebitAccountMissing, "WireDebitAccountMissing");
        }

        var debitAccount = WireIban.Normalize(profile.Iban);
        if (!WireIban.IsValid(debitAccount))
        {
            // NEVER log the IBAN itself (ADR-012-R2)
            logger.LogWarning("The issuer profile of org {OrgId} has an invalid IBAN (validation failed)", orgId);
            throw new DomainRuleException(WireTransferErrors.DebitAccountInvalid, "InvalidDebitAccountIban");
        }

        // The Swiss file is a domestic payment: a Swiss bank executes the debit of an account it holds, a Swiss or Liechtenstein IBAN, and the file names that bank by its
        // institution identifier. A foreign debit IBAN would leave the bank unnamed and a payment no Swiss bank executes, so the batch is refused. EUR is unchanged: its
        // debtor agent is «not provided» and the bank reads the account from the IBAN, whatever bank it is.
        if (flavor == Pain001Flavor.Swiss && !WireIban.IsSwiss(debitAccount))
        {
            // NEVER log the IBAN itself (ADR-012-R2)
            logger.LogWarning("The issuer profile of org {OrgId} has a debit IBAN that is not Swiss or Liechtenstein, which a CHF batch needs", orgId);
            throw new DomainRuleException(WireTransferErrors.DebitIbanNotSwiss, "WireDebitIbanNotSwiss");
        }

        // The creditors: every owner of the batch with a complete structured address and an IBAN that is a creditor account of a normal transfer in the area of the file.
        var creditors = await CreditorsAsync(orgId, payouts, flavor, cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var ordered = payouts.OrderBy(p => p.ConfirmedAt).ThenBy(p => p.Id).ToList();
        var transfers = ordered
            .Select(p => new Pain001Transfer(
                WireBatchPayout.EndToEndIdOf(p.Id),
                p.NetAmount,
                creditors[p.PropertyOwnerId].Party,
                creditors[p.PropertyOwnerId].Iban,
                RemittanceOf(versions[p.OwnerStatementVersionId].PeriodYear, versions[p.OwnerStatementVersionId].PeriodMonth),
                executionDate))
            .ToList();

        // The batch is built in memory: nothing is added to the context until its file has been validated.
        var batch = new WireBatch
        {
            OrgId = orgId,
            Currency = currency,
            DebitAccount = debitAccount,
            TotalAmount = transfers.Sum(t => t.Amount),
            TransferCount = transfers.Count,
            Status = WireBatchStatus.Generated,
            CreatedAt = now,
            ExecutionDate = executionDate,
            MessageFormat = WireMessageFormats.Of(currency),
        };

        // CHF is the Swiss pain.001.001.09.ch.03 file, EUR the pain.001.001.09 file of the SEPA credit transfer: the writer takes the flavor from the currency.
        var message = new Pain001Message(MessageIdOf(batch.Id), RomeCalendar.ToRome(now).DateTime, currency, debtor, debitAccount, transfers);
        var xmlContent = Pain001V09Writer.Write(message);

        // Validate before anything is saved: a file that does not validate leaves no batch behind, and with no schema there is no file.
        ValidateFile(xmlContent, batch);

        // One save for the batch, its payouts and the file it was validated for: all or nothing. A child with its own Guid goes through its set (added to a
        // collection alone it would be tracked as Modified).
        batch.XmlContent = xmlContent;
        db.WireBatches.Add(batch);
        for (var i = 0; i < ordered.Count; i++)
        {
            db.WireBatchPayouts.Add(new WireBatchPayout
            {
                OrgId = orgId,
                WireBatchId = batch.Id,
                OwnerPayoutId = ordered[i].Id,
                PropertyOwnerId = ordered[i].PropertyOwnerId,
                Amount = ordered[i].NetAmount,
                EndToEndId = transfers[i].EndToEndId,
                CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, WireBatchPayout.PayoutIndexName))
        {
            // Two requests for the same payouts that the lock could not tell apart: the index refused the second.
            throw new DomainRuleException(WireTransferErrors.PayoutsAlreadyInBatch, "PayoutsAlreadyInBatch");
        }

        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Created wire batch {BatchId} ({Format}) with {TransferCount} transfers for {TotalAmount:N2} {Currency}",
            batch.Id,
            batch.MessageFormat,
            batch.TransferCount,
            batch.TotalAmount,
            batch.Currency);

        return (batch, xmlContent);
    }

    /// <inheritdoc />
    public async Task ConfirmDownloadAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(orgId, batchId, cancellationToken);

        if (batch.Status != WireBatchStatus.Generated)
        {
            throw new DomainRuleException("batch_already_downloaded_or_paid", "BatchAlreadyDownloadedOrPaid");
        }

        await EnsureNoReversedPayoutsAsync(orgId, batch.Id, cancellationToken);

        batch.Status = WireBatchStatus.Downloaded;
        batch.DownloadedAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ConfirmPaidAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(orgId, batchId, cancellationToken);

        if (batch.Status == WireBatchStatus.Generated)
        {
            throw new DomainRuleException("batch_not_downloaded", "BatchNotDownloaded");
        }

        if (batch.Status == WireBatchStatus.Paid)
        {
            throw new DomainRuleException("batch_already_paid", "BatchAlreadyPaid");
        }

        // A batch is never marked paid while its file would still pay a payout that was reversed after the batch was made.
        await EnsureNoReversedPayoutsAsync(orgId, batch.Id, cancellationToken);

        batch.Status = WireBatchStatus.Paid;
        batch.PaidAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(WireBatch Batch, string XmlContent)> GetBatchFileAsync(
        Guid orgId,
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await FindBatchAsync(orgId, batchId, cancellationToken);

        if (string.IsNullOrEmpty(batch.XmlContent))
        {
            throw new DomainRuleException("batch_xml_not_found", "BatchXmlNotFound");
        }

        // The file of a batch that is not paid yet is not handed out while it pays a payout that was reversed after the batch was made: it would go to the bank
        // and pay it. A paid batch is evidence of what was sent and stays readable.
        if (batch.Status != WireBatchStatus.Paid)
        {
            await EnsureNoReversedPayoutsAsync(orgId, batch.Id, cancellationToken);
        }

        // Mark as downloaded on first retrieval
        if (batch.Status == WireBatchStatus.Generated)
        {
            batch.Status = WireBatchStatus.Downloaded;
            batch.DownloadedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return (batch, batch.XmlContent);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WireBatchSummary>> ListBatchesAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        // The columns are chosen one by one: the file and the debit account are never read, so never decrypted and never in a list.
        return await db.WireBatches.AsNoTracking()
            .Where(b => b.OrgId == orgId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .Take(WireTransferErrors.ListedBatches)
            .Select(b => new WireBatchSummary(
                b.Id,
                b.Currency,
                b.Status,
                b.TransferCount,
                b.TotalAmount,
                b.ExecutionDate,
                b.MessageFormat,
                b.CreatedAt,
                b.DownloadedAt,
                b.PaidAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WirePayoutSummary>> ListPayablePayoutsAsync(Guid orgId, string? currency, CancellationToken cancellationToken = default)
    {
        // One list per currency: there is no list of every currency, and a currency with no wire file has none (the refusal of a batch of such payouts).
        var wanted = NormalizeCurrency(currency);
        if (wanted is not (WireTransferErrors.SepaCurrency or WireTransferErrors.SwissCurrency))
        {
            throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported");
        }

        // The conditions under which CreateBatchAsync accepts a payout, as a read: confirmed (a reversed payout is never payable), of a version published to the owner, with
        // a net amount a transfer can carry (above zero), in the currency asked for (the stored code is trimmed and in upper case, as a batch reads it) and in no batch.
        // Every table is filtered on the org explicitly, besides the global filter of the context. Oldest first: the order of the transfers of a batch.
        var rows = await (
                from payout in db.OwnerPayouts.AsNoTracking()
                join version in db.OwnerStatementVersions.AsNoTracking() on payout.OwnerStatementVersionId equals version.Id
                join owner in db.PropertyOwners.AsNoTracking() on payout.PropertyOwnerId equals owner.Id
                where payout.OrgId == orgId
                    && version.OrgId == orgId
                    && owner.OrgId == orgId
                    && payout.Status == OwnerPayoutStatus.Confirmed
                    && version.PublishedAt != null
                    && payout.NetAmount > 0m
                    && payout.Currency.Trim().ToUpper() == wanted
                    && !db.WireBatchPayouts.Any(w => w.OrgId == orgId && w.OwnerPayoutId == payout.Id)
                orderby payout.ConfirmedAt, payout.Id
                select new PayoutRow
                {
                    PayoutId = payout.Id,
                    OwnerId = owner.Id,
                    OwnerLegalName = owner.LegalName,
                    OwnerFirstName = owner.FirstName,
                    OwnerLastName = owner.LastName,
                    OwnerIban = owner.Iban,
                    PeriodYear = version.PeriodYear,
                    PeriodMonth = version.PeriodMonth,
                    Amount = payout.NetAmount,
                    Currency = payout.Currency,
                    ConfirmedAt = payout.ConfirmedAt,
                    Status = payout.Status,
                })
            .Take(WireTransferErrors.ListedPayablePayouts)
            .ToListAsync(cancellationToken);

        return rows.Select(SummaryOf).ToList();
    }

    /// <inheritdoc />
    public async Task<WireBatchPayoutsSummary> GetBatchPayoutsAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default)
    {
        // The batch of the org, by the columns of the list: the file and the debit account are never read, so never decrypted. The org is checked here, in the query: a batch
        // of another org does not exist for the caller (404, as if the id were a guess).
        var batch = await db.WireBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.OrgId == orgId)
            .Select(b => new WireBatchSummary(
                b.Id,
                b.Currency,
                b.Status,
                b.TransferCount,
                b.TotalAmount,
                b.ExecutionDate,
                b.MessageFormat,
                b.CreatedAt,
                b.DownloadedAt,
                b.PaidAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            throw BatchNotFound(batchId);
        }

        // The rows of the batch and what each one pays: the amount is the one of the transfer in the file, the payout says whether it was reversed after the batch was made.
        // The rows of a batch of before the payouts are statements, not payouts: there are none here. The order is the one of the transfers of the file.
        var rows = await (
                from link in db.WireBatchPayouts.AsNoTracking()
                join payout in db.OwnerPayouts.AsNoTracking() on link.OwnerPayoutId equals payout.Id
                join version in db.OwnerStatementVersions.AsNoTracking() on payout.OwnerStatementVersionId equals version.Id
                join owner in db.PropertyOwners.AsNoTracking() on link.PropertyOwnerId equals owner.Id
                where link.OrgId == orgId
                    && link.WireBatchId == batchId
                    && payout.OrgId == orgId
                    && version.OrgId == orgId
                    && owner.OrgId == orgId
                orderby payout.ConfirmedAt, payout.Id
                select new PayoutRow
                {
                    PayoutId = payout.Id,
                    OwnerId = owner.Id,
                    OwnerLegalName = owner.LegalName,
                    OwnerFirstName = owner.FirstName,
                    OwnerLastName = owner.LastName,
                    OwnerIban = owner.Iban,
                    PeriodYear = version.PeriodYear,
                    PeriodMonth = version.PeriodMonth,
                    Amount = link.Amount,
                    Currency = payout.Currency,
                    ConfirmedAt = payout.ConfirmedAt,
                    Status = payout.Status,
                })
            .ToListAsync(cancellationToken);

        return new WireBatchPayoutsSummary(batch, rows.Select(SummaryOf).ToList());
    }

    /// <summary>
    /// The creditor and the IBAN of every owner of the batch, or the refusal that lists the owners that cannot be paid with this file (their ids, never their names): an
    /// owner with no complete structured address (both files write it), with no IBAN, with an IBAN that fails the check digits, with a QR-IBAN (the account of a
    /// QR-bill, never the creditor account of a normal transfer) or with an IBAN outside the area of the file (in CHF, one that is not Swiss or Liechtenstein, a
    /// domestic payment; in EUR, one outside the SEPA area, which the SEPA credit transfer cannot reach). One refusal at a time, in that order.
    /// </summary>
    private async Task<Dictionary<Guid, (Pain001Party Party, string Iban)>> CreditorsAsync(
        Guid orgId,
        IReadOnlyList<OwnerPayout> payouts,
        Pain001Flavor flavor,
        CancellationToken cancellationToken)
    {
        var ownerIds = payouts.Select(p => p.PropertyOwnerId).Distinct().Order().ToList();
        var owners = await db.PropertyOwners.AsNoTracking()
            .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, cancellationToken);

        var swiss = flavor == Pain001Flavor.Swiss;
        var creditors = new Dictionary<Guid, (Pain001Party Party, string Iban)>();
        var withoutAddress = new List<Guid>();
        var withoutIban = new List<Guid>();
        var invalidIban = new List<Guid>();
        var qrIban = new List<Guid>();
        var notSwissIban = new List<Guid>();
        var notSepaIban = new List<Guid>();
        foreach (var ownerId in ownerIds)
        {
            owners.TryGetValue(ownerId, out var owner);
            var party = new Pain001Party(
                OwnerNameOf(owner),
                owner?.BillingStreet,
                owner?.BillingCivic,
                owner?.BillingPostalCode,
                owner?.BillingCity,
                owner?.BillingCountry);

            // Both files write the structured address of the creditor, so both ask for it (and for a name the message can write).
            if (!Pain001V09Writer.IsComplete(party, flavor))
            {
                withoutAddress.Add(ownerId);
            }

            var iban = WireIban.Normalize(owner?.Iban);
            if (iban.Length == 0)
            {
                withoutIban.Add(ownerId);
            }
            else if (!WireIban.IsValid(iban))
            {
                invalidIban.Add(ownerId);
            }
            else if (WireIban.IsQrIban(iban))
            {
                qrIban.Add(ownerId);
            }
            else if (swiss && !WireIban.IsSwiss(iban))
            {
                notSwissIban.Add(ownerId);
            }
            else if (!swiss && !WireIban.IsSepa(iban))
            {
                notSepaIban.Add(ownerId);
            }
            else
            {
                creditors[ownerId] = (party, iban);
            }
        }

        if (withoutAddress.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerAddressMissing, "WireOwnerAddressMissing", withoutAddress);
        }

        if (withoutIban.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerIbanMissing, "WireOwnerIbanMissing", withoutIban);
        }

        if (invalidIban.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerIbanInvalid, "OwnerInvalidIban", invalidIban);
        }

        if (qrIban.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerQrIban, "WireOwnerQrIban", qrIban);
        }

        if (notSwissIban.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerIbanNotSwiss, "WireOwnerIbanNotSwiss", notSwissIban);
        }

        if (notSepaIban.Count > 0)
        {
            throw OwnersRefused(WireTransferErrors.OwnerIbanNotSepa, "WireOwnerIbanNotSepa", notSepaIban);
        }

        return creditors;
    }

    /// <summary>
    /// The refusal of a batch for some owners: 422 with the code and, in <c>ownerIds</c>, the ids of the owners (a number of them is the message argument). The log
    /// carries the code, how many and their ids, never a name or an IBAN.
    /// </summary>
    private DomainRuleException OwnersRefused(string code, string messageKey, IReadOnlyList<Guid> ownerIds)
    {
        logger.LogWarning(
            "Wire batch refused ({Code}) for {OwnerCount} owners, first ids: {OwnerIds}",
            code,
            ownerIds.Count,
            string.Join(", ", ownerIds.Take(20)));

        return new DomainRuleException(code, messageKey, ownerIds.Count)
        {
            Extensions = new Dictionary<string, object?> { ["ownerIds"] = ownerIds },
        };
    }

    private async Task<WireBatch> FindBatchAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken)
    {
        // The org is checked here, in the query: a Find by key does not apply the global filter of the context, and a batch of another org must not be reachable
        // through a guessed id (404, as if it did not exist).
        var batch = await db.WireBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.OrgId == orgId, cancellationToken);
        return batch ?? throw BatchNotFound(batchId);
    }

    /// <summary>The refusal for a batch that is not the caller's org's, or does not exist: 404, never a 403, so that an id cannot be probed.</summary>
    private static NotFoundException BatchNotFound(Guid batchId)
    {
        return new NotFoundException($"Wire batch {batchId} not found");
    }

    /// <summary>
    /// Refuses (422 <c>wire_batch_payouts_reversed</c>, with the ids of the payouts in <c>payoutIds</c>) a step of a batch whose file would still pay a payout that was
    /// reversed after the batch was made. A reversal posts the opposite ledger lines and does not know about the batches, and a batch cannot be cancelled yet: the file
    /// stays as it was written, so the batch is neither handed out nor marked as paid until the payouts are checked.
    /// </summary>
    private async Task EnsureNoReversedPayoutsAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken)
    {
        var reversed = await (
                from link in db.WireBatchPayouts.AsNoTracking()
                join payout in db.OwnerPayouts.AsNoTracking() on link.OwnerPayoutId equals payout.Id
                where link.OrgId == orgId && link.WireBatchId == batchId && payout.OrgId == orgId && payout.Status == OwnerPayoutStatus.Reversed
                orderby link.OwnerPayoutId
                select link.OwnerPayoutId)
            .ToListAsync(cancellationToken);
        if (reversed.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Wire batch {BatchId} pays {PayoutCount} payouts that were reversed after it was made, first ids: {PayoutIds}",
            batchId,
            reversed.Count,
            string.Join(", ", reversed.Take(20)));

        throw new DomainRuleException(WireTransferErrors.BatchPayoutsReversed, "WireBatchPayoutsReversed", reversed.Count)
        {
            Extensions = new Dictionary<string, object?> { ["payoutIds"] = reversed },
        };
    }

    /// <summary>The name an owner is paid under: the legal name when the registry has one, else the first and last name.</summary>
    private static string OwnerNameOf(PropertyOwner? owner)
    {
        if (owner is null)
        {
            return string.Empty;
        }

        return OwnerNameOf(owner.LegalName, owner.FirstName, owner.LastName);
    }

    /// <summary>The name an owner is paid under, from the columns of the registry: the legal name when there is one, else the first and last name.</summary>
    private static string OwnerNameOf(string? legalName, string firstName, string lastName)
    {
        return string.IsNullOrWhiteSpace(legalName) ? $"{firstName} {lastName}" : legalName;
    }

    /// <summary>
    /// A payout of the wire page as a row: the owner from its columns and the IBAN of the owner, which is read (decrypted) only so that the page can be told whether there is
    /// one. Never written out: it is a class and not a record because a record prints all its members.
    /// </summary>
    private static WirePayoutSummary SummaryOf(PayoutRow row)
    {
        return new WirePayoutSummary(
            row.PayoutId,
            row.OwnerId,
            OwnerNameOf(row.OwnerLegalName, row.OwnerFirstName, row.OwnerLastName),
            row.PeriodYear,
            row.PeriodMonth,
            row.Amount,
            NormalizeCurrency(row.Currency),
            row.ConfirmedAt,
            row.Status,
            WireIban.Normalize(row.OwnerIban).Length > 0);
    }

    /// <summary>The remittance of a transfer: «Rendiconto» and the period of the statement it pays, <c>Rendiconto 10/2026</c>.</summary>
    private static string RemittanceOf(int year, int month)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Rendiconto {month:D2}/{year:D4}");
    }

    /// <summary>The message id of the file of a batch: <c>CZ</c> and its id without hyphens (34 characters, unique).</summary>
    private static string MessageIdOf(Guid batchId)
    {
        return WireBatchPayout.EndToEndIdPrefix + batchId.ToString("N");
    }

    /// <summary>
    /// Validate the file of a batch before the batch is saved (E06/F05/US01/T01, NF-25): against the official schema of its currency (EUR: ISO,
    /// CHF: Swiss; no DTD, no resolver, no network), then against what a schema cannot say, the sums and the SEPA rules of the EUR file. 422 <c>wire_batch_schema_unavailable</c> when the
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

    /// <summary>The currency code of a payout: trimmed and in upper case (the stored one is a free text of three letters).</summary>
    private static string NormalizeCurrency(string? currency)
    {
        return (currency ?? string.Empty).Trim().ToUpperInvariant();
    }

    /// <summary>What the reads of the wire page select for a payout (see <see cref="SummaryOf"/>).</summary>
    private sealed class PayoutRow
    {
        public Guid PayoutId { get; set; }

        public Guid OwnerId { get; set; }

        public string? OwnerLegalName { get; set; }

        public string OwnerFirstName { get; set; } = string.Empty;

        public string OwnerLastName { get; set; } = string.Empty;

        public string? OwnerIban { get; set; }

        public int PeriodYear { get; set; }

        public int PeriodMonth { get; set; }

        public decimal Amount { get; set; }

        public string Currency { get; set; } = string.Empty;

        public DateTime ConfirmedAt { get; set; }

        public OwnerPayoutStatus Status { get; set; }
    }
}

/// <summary>
/// The IBANs of the wire files (E16/F01/US01/T01, E16/F01/US02/T01), on the definitions the rest of CasaZen already has (the owner registry and the issuer profile):
/// the normal form, the length of the country and the check digits of ISO 13616 (<see cref="OwnerFieldRules"/>, which is <c>BankValues.IsValidIban</c>), the QR-IBAN of
/// the QR-bill, never the creditor account of a normal transfer (<see cref="IssuerBankAccounts.IsQrIban"/>), and the SEPA area of the registry
/// (<see cref="OwnerFieldRules.IsSepaIban"/>, where the IBAN alone is enough); the Swiss or Liechtenstein IBAN of a domestic CHF payment is the one rule that is the
/// wire's own. None of them logs: an IBAN is never written anywhere but in the file.
/// </summary>
internal static class WireIban
{
    /// <summary>An IBAN with no space and no hyphen, in upper case; empty for a missing one.</summary>
    public static string Normalize(string? iban)
    {
        return OwnerFieldRules.NormalizeIban(iban);
    }

    /// <summary>True for an IBAN in its normal form that has the length of its country and passes the check digits (ISO 13616, mod 97 equal to 1).</summary>
    public static bool IsValid(string iban)
    {
        return !string.IsNullOrEmpty(iban) && OwnerFieldRules.IsValidIban(iban);
    }

    /// <summary>True for a Swiss or Liechtenstein IBAN (21 characters).</summary>
    public static bool IsSwiss(string iban)
    {
        return iban.Length == 21 && (iban.StartsWith("CH", StringComparison.Ordinal) || iban.StartsWith("LI", StringComparison.Ordinal));
    }

    /// <summary>True for an IBAN of the SEPA area (the registry's definition): the creditor account of a SEPA credit transfer, which a bank reaches with the IBAN alone.</summary>
    public static bool IsSepa(string iban)
    {
        return OwnerFieldRules.IsSepaIban(iban);
    }

    /// <summary>True for a QR-IBAN: a Swiss or Liechtenstein IBAN whose institution identifier (characters 5 to 9) is between 30000 and 31999.</summary>
    public static bool IsQrIban(string iban)
    {
        return IssuerBankAccounts.IsQrIban(iban);
    }
}
