using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The self-billed invoices (TD17, E17/F01/US03/T01): the issue and the discard of a draft made by <see cref="ISelfBillingService"/>, and the FatturaPA input of one. The
/// draft keeps the foreign supplier (the cedente) in the recipient columns of the document, the counterparty a list and an export show, and the holder that issues it
/// (the cessionario) as the issuer of the document: its profile numbers it, its snapshot is kept at the issue and the agency is the intermediary of an owner company's.
/// </summary>
public sealed partial class FiscalDocumentService
{
    public async Task<FiscalDocument> IssueSelfBillingAsync(Guid documentId, FiscalTaxRule rule, string seriesPattern, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        RequireSelfBilling(document);
        if (document.IssuerProfileId is not { } holderProfileId || document.PeriodYear is null || document.PeriodMonth is null)
            throw NotFound(documentId);

        // The profile the draft was made for: the agency's own (it holds the account) or an owner's (an owner company issues through the agency). A profile that is gone is a
        // draft whose holder no longer exists: it is never issued in the name of another.
        var (organization, org) = await RequireIssuerAsync(document.OrgId, ct);
        var holder = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == holderProfileId && p.OrgId == document.OrgId, ct)
            ?? throw new DomainRuleException(FiscalDocumentErrors.OwnerIssuerMissing, "FiscalDocumentOwnerIssuerMissing");

        var today = timeProvider.TodayInRomeAsDateOnly();
        var series = FiscalNumberFormat.SeriesFor(seriesPattern, today);
        var parties = holder.IsOwnerProfile ? new IssuerParties(FiscalIssuerIntermediary.Of(organization, org), null) : null;
        await IssueInTransactionAsync(document, document.OrgId, bookingId: null, rule, holder, org, series, today, credited: null, ct, parties: parties);
        logger.LogInformation("Self-billed invoice {DocumentId} issued by {UserId}", document.Id, userId);
        return document;
    }

    public async Task DiscardSelfBillingDraftAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        RequireSelfBilling(document);

        // The replacement of a TD17 the SDI rejected (FIX/BE-TD17-CORRECTION) holds the number that TD17 gave up: discarded, the number would be lost (a hole in the numbering of the series) and the rejected
        // TD17 could not be corrected again. It is issued, never discarded: 422 document_replacement_holds_number.
        RequireNotAHeldNumber(document);
        document.Status = FiscalDocumentStatus.Discarded;
        document.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Self-billed invoice draft {DocumentId} discarded", document.Id);
    }

    /// <summary>The self-billing path takes only a self-billed invoice: any other document is not found there.</summary>
    private static void RequireSelfBilling(FiscalDocument document)
    {
        if (document.Purpose != FiscalDocumentPurpose.SelfBilling)
            throw NotFound(document.Id);
    }

    /// <summary>
    /// The input of the XML of an issued TD17: the foreign supplier of the recipient columns is the cedente (its country and VAT number, regime <c>RF18</c>, an address abroad),
    /// the holder of the snapshot is the cessionario, that issues the document itself (<c>SoggettoEmittente</c> <c>CC</c>), and receives it at its own SDI code or PEC
    /// (<c>RecipientSdiCode</c> and <c>RecipientPec</c> of the document, taken from the holder's profile when the draft was made). The transmitter is the subject whose channel
    /// sends the file (<see cref="TransmitterOf"/>): the agency, for an owner company too. No related document: the invoice of the portal is not held by CasaZen.
    /// </summary>
    internal static FatturaPaInput SelfBillingFatturaPaInputOf(FiscalDocument document, FiscalIssuerSnapshot holder)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(holder);

        var lines = AmountsOf(document);
        var summary = FiscalDocumentTotals.SummaryOf(lines, document.VatAmount);

        var supplierCountry = CountryOf(document.RecipientCountryCode);
        var supplier = new FatturaPaParty(
            new FatturaPaId(supplierCountry, SelfBillingRules.VatCodeOf(document.RecipientVatNumber, supplierCountry) ?? string.Empty),
            null,
            document.RecipientName,
            null,
            null,
            new FatturaPaAddress(
                document.RecipientAddressStreet ?? string.Empty,
                document.RecipientAddressCivic,
                FatturaPaParties.ForeignPostalCode,
                document.RecipientCity ?? string.Empty,
                null,
                supplierCountry),
            SelfBillingRules.ForeignSupplierRegimeCode);

        var holderCountry = CountryOf(holder.CountryCode);
        var holderVatNumber = DigitsOf(holder.VatNumber);
        var buyer = new FatturaPaParty(
            holderVatNumber is null ? null : new FatturaPaId(holderCountry, holderVatNumber),
            holder.FiscalCode,
            holder.DisplayName,
            null,
            null,
            new FatturaPaAddress(
                holder.AddressStreet ?? string.Empty,
                holder.AddressCivic,
                holder.PostalCode ?? string.Empty,
                holder.City ?? string.Empty,
                holder.Province,
                holderCountry));

        return new FatturaPaInput(
            TransmitterOf(holder),
            document.Id.ToString("N")[..10].ToUpperInvariant(),
            supplier,
            buyer,
            document.RecipientSdiCode,
            document.RecipientPec,
            SelfBillingRules.DocumentTypeCode,
            document.Number!,
            document.IssueDate!.Value,
            document.Currency,
            lines,
            summary,
            document.StampDuty,
            document.Total,
            null)
        {
            IssuedBy = FatturaPaIssuedBy.Recipient,
        };
    }
}
