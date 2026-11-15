using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalDocumentDeliveryService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Queued, never sent here.</b> The e-mail goes through <see cref="IEmailQueue"/> (Hangfire, retried by the job), so a slow provider cannot turn an issued document into an error. A queue that
/// says no (no provider configured, no public address of the web app, the queue is down) is an error of the send, never a success: <c>document_email_not_sent</c>. The queue has no attachments, so the
/// e-mail carries the link, not the PDF. The automatic send tells an environment that cannot send at all (<see cref="FiscalDocumentSendOutcome.NotConfigured"/>) from a queue that refuses while it could.</item>
/// <item><b>Nothing personal outside the e-mail.</b> The e-mail holds the first name of the guest, the name of the issuer, the kind, the number and the day of the document, the link and its
/// last day: no amount, no address, no fiscal data, and no name of a portal or of a supplier. Every value is HTML-encoded by <see cref="EmailHtmlBuilder"/>. The logs carry ids and the expiry
/// of the link: never the address, the name of the guest or the token.</item>
/// <item><b>The public reads are tenant-free on purpose.</b> The document is found by the signed id with the tenant filter ignored (the visitor has no org, and a signed-in person of another org who
/// opens a link must see what the guest sees), and the PDF is drawn by <see cref="IFiscalDocumentService"/> in a scope of its own entered under the org of the document, as the jobs and the event bus do.</item>
/// <item><b>The language of the e-mail</b> is the one of the guest by country (<see cref="MessageReplyService.EmailCultureOf"/>): Italian for Italy and for no country, English for everybody else.</item>
/// <item><b>The automatic send is queued once for each delivery of its event.</b> The event reaches the consumer at least once: another consumer of it that fails, or a stop of the dispatcher before it saves,
/// delivers it again, and the second delivery would queue a second e-mail. This process therefore remembers for an hour (<see cref="AutomaticSendMemory"/>, in memory, like <see cref="MessageReplyService.EmailKeyMemory"/>:
/// nothing is stored, no column, no migration) the documents whose e-mail it queued by itself, and answers <see cref="FiscalDocumentSendOutcome.AlreadyQueued"/> to a later delivery. What a queue that refuses, or an
/// error, left undone is never remembered, so it is tried again. <b>The limit</b>: another replica, or a restart within the hour, does not know it and can queue a second e-mail (with a link of its own). The host's
/// own send is never held back: every call sends again.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentDeliveryService(
    AppDbContext db,
    IEmailQueue emailQueue,
    IOptions<EmailOptions> emailOptions,
    PublicSiteLinks links,
    FiscalDocumentLinkTokens tokens,
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentDeliveryService> logger) : IFiscalDocumentDeliveryService
{
    /// <summary>
    /// The domains the standards keep for what is never delivered: <c>.invalid</c> (RFC 6761), <c>.localhost</c> and <c>.local</c> (the placeholder of an erased guest, <c>ANON-…@deleted.local</c>, is
    /// one). The names that are only for tests and documentation (<c>example.com</c>, <c>.test</c>) stay addresses: the data of a demo is e-mailed like any other.
    /// </summary>
    private static readonly string[] NeverDeliveredSuffixes = [".invalid", ".localhost", ".local"];

    /// <summary>How long this process remembers that it queued the e-mail of a document by itself, so that a second delivery of the issue event queues nothing (see the remarks).</summary>
    public static readonly TimeSpan AutomaticSendMemory = TimeSpan.FromHours(1);

    public async Task<FiscalDocumentDeliveryResult> SendToGuestAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await ReadAsync(documentId, ct);
        RequireSendable(document);
        return await QueueAsync(document, ct);
    }

    public async Task<FiscalDocumentSendOutcome> SendAutomaticallyAsync(Guid documentId, CancellationToken ct = default)
    {
        // The event reaches the consumer at least once: this process remembers the documents whose e-mail it queued by itself, so that a second delivery (another consumer of the event failed, or the
        // dispatcher stopped before it saved) queues nothing. Only a queued e-mail is remembered: what a refusing queue or an error left undone is tried again by the next delivery.
        var memory = new AutomaticSendKey(documentId);
        if (cache.TryGetValue(memory, out _))
        {
            logger.LogInformation("Fiscal document {DocumentId} was already e-mailed to the guest by an earlier delivery of its event: nothing is queued again", documentId);
            return FiscalDocumentSendOutcome.AlreadyQueued;
        }

        var document = await ReadAsync(documentId, ct);
        if (!IsSendable(document))
            return FiscalDocumentSendOutcome.NotSendable;

        // The choice of the organization itself, never the profile of one of its owners (E17/F01/US01/T01: they live in the same table).
        var asked = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .OfOrganization(document.OrgId)
            .Select(p => p.EmailGuestDocuments)
            .FirstOrDefaultAsync(ct);
        if (!asked)
            return FiscalDocumentSendOutcome.NotAsked;

        // An environment with no e-mail provider or no public address of the web app cannot send, whatever is tried again: that is its state, not a failure to deliver (outside Development and Testing the
        // startup refuses such a configuration). A queue that says no while both are there is a failure, and is thrown for the outbox to deliver the event again.
        if (!emailOptions.Value.IsConfigured || !links.IsConfigured)
        {
            logger.LogWarning("Fiscal document {DocumentId} of org {OrgId} was not e-mailed: the e-mail provider or the public address of the web app is not configured", document.Id, document.OrgId);
            return FiscalDocumentSendOutcome.NotConfigured;
        }

        try
        {
            await QueueAsync(document, ct);
            cache.Set(memory, true, AutomaticSendMemory);
            return FiscalDocumentSendOutcome.Queued;
        }
        catch (DomainRuleException ex) when (ex.Code == FiscalDocumentDeliveryErrors.RecipientEmailInvalid)
        {
            logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} was not e-mailed: the guest has no address to send it to", document.Id, document.OrgId);
            return FiscalDocumentSendOutcome.NoAddress;
        }
    }

    public async Task<FiscalDocumentLinkSummary?> GetSummaryByLinkAsync(Guid documentId, string? token, CancellationToken ct = default)
    {
        var document = await OpenAsync(documentId, token, ct);
        return document is null
            ? null
            : new FiscalDocumentLinkSummary(document.Id, document.Kind, document.Number!, document.IssueDate!.Value, document.Total, document.Currency);
    }

    public async Task<FiscalDocumentLinkFile?> GetPdfByLinkAsync(Guid documentId, string? token, string language, CancellationToken ct = default)
    {
        var document = await OpenAsync(documentId, token, ct);
        if (document is null)
            return null;

        // The one service that knows how to draw the document, as the host sees it, under the tenant of the document: the scope is new, so the person who opens the link, signed in or not, changes nothing
        // of what it reads (the same way the jobs and the handlers of the event bus run).
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(document.OrgId);
            var pdf = await scope.ServiceProvider.GetRequiredService<IFiscalDocumentService>().GetPdfAsync(document.Id, language, ct);
            logger.LogInformation("Fiscal document {DocumentId} of org {OrgId}: the PDF was read with its link", document.Id, document.OrgId);
            return new FiscalDocumentLinkFile(pdf, FileNameOf(document.Number!));
        }
        catch (NotFoundException)
        {
            // The document went away between the two reads: it is the same answer as for any link that does not open.
            return null;
        }
    }

    // ---------------------------------------------------------------- the e-mail

    /// <summary>The document as the send needs it: what it is, whose it is and what it says; read with the tenant filter of the caller.</summary>
    private async Task<DocumentFacts> ReadAsync(Guid documentId, CancellationToken ct)
    {
        var document = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new DocumentFacts(d.Id, d.OrgId, d.BookingId, d.Source, d.Purpose, d.Status, d.Kind, d.Number, d.IssueDate, d.IssuerSnapshot))
            .FirstOrDefaultAsync(ct);

        return document
               ?? throw new NotFoundException($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
    }

    private static bool IsSendable(DocumentFacts document) =>
        document.Source == FiscalDocumentSource.CasaZen
        && document.Purpose == FiscalDocumentPurpose.GuestStay
        && document.BookingId is not null
        && document.Status == FiscalDocumentStatus.Issued;

    /// <summary>The refusals of the host's send, in the order a person fixes them: what the document is, then what state it is in.</summary>
    private static void RequireSendable(DocumentFacts document)
    {
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw new DomainRuleException(FiscalDocumentErrors.ReadOnly, "FiscalDocumentReadOnly");

        if (document.Purpose != FiscalDocumentPurpose.GuestStay || document.BookingId is null)
            throw new DomainRuleException(OwnerInvoiceErrors.UseOwnerInvoices, "FiscalDocumentOwnerInvoice");

        if (document.Status != FiscalDocumentStatus.Issued)
            throw new DomainRuleException(FiscalDocumentDeliveryErrors.NotIssued, "FiscalDocumentNotIssued");
    }

    /// <summary>The e-mail with the link of <paramref name="document"/>, queued for the guest of its booking.</summary>
    private async Task<FiscalDocumentDeliveryResult> QueueAsync(DocumentFacts document, CancellationToken ct)
    {
        var guest = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == document.BookingId)
            .Select(b => new GuestFacts(b.Guest.FirstName, b.Guest.Email, b.Guest.Country, b.Guest.DataAnonymizedDate))
            .FirstOrDefaultAsync(ct);
        if (guest is null || !TryUseAddress(guest.Email, guest.AnonymizedAt, out var address))
            throw new DomainRuleException(FiscalDocumentDeliveryErrors.RecipientEmailInvalid, "DocumentRecipientEmailInvalid");

        // Without the public address of the web app there is no link to write: nothing is sent, and the answer says so.
        if (!links.IsConfigured)
        {
            logger.LogWarning("Fiscal document {DocumentId} of org {OrgId} was not e-mailed: the public address of the web app is not configured", document.Id, document.OrgId);
            throw NotSent();
        }

        var expiresAt = timeProvider.GetUtcNow() + FiscalDocumentLinkRules.Lifetime;
        var url = links.FiscalDocumentView(document.Id, tokens.Create(document.Id, expiresAt));
        var content = EmailTemplates.FiscalDocumentDelivery(
            MessageReplyService.EmailCultureOf(guest.Country),
            guest.FirstName,
            await IssuerNameAsync(document, ct),
            document.Kind,
            document.Number ?? string.Empty,
            (document.IssueDate ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)).ToDateTime(TimeOnly.MinValue),
            url,
            expiresAt.UtcDateTime);

        if (!emailQueue.Enqueue(address, content, EmailTemplates.Names.FiscalDocumentDelivery))
        {
            logger.LogWarning("The e-mail with the fiscal document {DocumentId} of booking {BookingId} (org {OrgId}) could not be queued", document.Id, document.BookingId, document.OrgId);
            throw NotSent();
        }

        logger.LogInformation(
            "Fiscal document {DocumentId} of booking {BookingId} (org {OrgId}) queued to the guest, the link expires at {ExpiresAt}",
            document.Id,
            document.BookingId,
            document.OrgId,
            expiresAt.UtcDateTime);
        return new FiscalDocumentDeliveryResult(document.Id, expiresAt.UtcDateTime);
    }

    /// <summary>The name the e-mail says the document comes from: the issuer as it was at the issue, the organization for a document issued before the snapshot existed.</summary>
    private async Task<string> IssuerNameAsync(DocumentFacts document, CancellationToken ct)
    {
        var name = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return await db.Orgs.AsNoTracking().Where(o => o.Id == document.OrgId).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
    }

    /// <summary>
    /// The address an e-mail to the guest goes to: the one of the booking, trimmed, when it is a real address. False for an erased guest, an empty or malformed address, one without a dot in its domain
    /// and one on a domain that is never delivered (<see cref="NeverDeliveredSuffixes"/>). A relay address of a portal (<c>…@guest.booking.com</c>) is a normal address: it forwards to the guest.
    /// </summary>
    internal static bool TryUseAddress(string? email, DateTime? anonymizedAt, out string address)
    {
        address = string.Empty;
        if (anonymizedAt is not null || string.IsNullOrWhiteSpace(email))
            return false;

        var candidate = email.Trim();
        if (!EmailOptions.IsValidAddress(candidate))
            return false;

        var domain = candidate[(candidate.LastIndexOf('@') + 1)..].ToLowerInvariant();
        if (!domain.Contains('.') || NeverDeliveredSuffixes.Any(suffix => domain.EndsWith(suffix, StringComparison.Ordinal)))
            return false;

        address = candidate;
        return true;
    }

    private static DomainRuleException NotSent() => new(FiscalDocumentDeliveryErrors.EmailNotSent, "FiscalDocumentEmailNotSent");

    // ---------------------------------------------------------------- the public link

    /// <summary>
    /// The document a link opens, or <c>null</c>: one answer for every reason it does not (see <see cref="IFiscalDocumentDeliveryService"/>). The token is read first and costs no query; the
    /// document is then found by the id the token was made for, with the tenant filter ignored, and only an issued guest document of CasaZen opens.
    /// </summary>
    private async Task<LinkFacts?> OpenAsync(Guid documentId, string? token, CancellationToken ct)
    {
        if (!tokens.TryRead(token, out var tokenDocumentId) || tokenDocumentId != documentId)
            return null;

        var document = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Id == documentId
                        && d.Source == FiscalDocumentSource.CasaZen
                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Status == FiscalDocumentStatus.Issued)
            .Select(d => new LinkFacts(d.Id, d.OrgId, d.Kind, d.Number, d.IssueDate, d.Total, d.Currency))
            .FirstOrDefaultAsync(ct);

        // An issued document always has its number and its day: one that has not is not one a link opens.
        return document is { Number: not null, IssueDate: not null } ? document : null;
    }

    /// <summary>The name of the PDF: <c>documento-</c> and the number with every character that is not a letter, a digit or a dash made a dash (<c>2026/12</c> is <c>2026-12</c>).</summary>
    private static string FileNameOf(string number) =>
        "documento-" + new string(number.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()) + ".pdf";

    private sealed record DocumentFacts(
        Guid Id,
        Guid OrgId,
        Guid? BookingId,
        FiscalDocumentSource Source,
        FiscalDocumentPurpose Purpose,
        FiscalDocumentStatus Status,
        FiscalDocumentKind Kind,
        string? Number,
        DateOnly? IssueDate,
        string? IssuerSnapshot);

    private sealed record GuestFacts(string FirstName, string Email, string Country, DateTime? AnonymizedAt);

    /// <summary>The key of the memory of the documents whose e-mail was queued by the automatic send (see <see cref="AutomaticSendMemory"/>).</summary>
    private sealed record AutomaticSendKey(Guid DocumentId);

    private sealed record LinkFacts(Guid Id, Guid OrgId, FiscalDocumentKind Kind, string? Number, DateOnly? IssueDate, decimal Total, string Currency);
}
