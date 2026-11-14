using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the documents of a booking do when the booking changes (E12/F01/US01/T01): a draft follows the booking, an issued document never does.
/// </summary>
public sealed partial class FiscalDocumentService
{
    public async Task<BookingChangeOutcome> ApplyBookingChangeAsync(Guid bookingId, BookingChange change, CancellationToken ct = default)
    {
        BookingFacts booking;
        try
        {
            booking = await BookingOfAsync(bookingId, ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Booking {BookingId} not found: no document to follow its change ({Change})", bookingId, change);
            return BookingChangeOutcome.None;
        }

        var active = IsActive(booking);
        var documents = await db.FiscalDocuments
            .Include(d => d.Lines)
            .Where(d => d.BookingId == bookingId
                        && d.Source == FiscalDocumentSource.CasaZen
                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Status != FiscalDocumentStatus.Discarded
                        && d.Kind != DocumentKind.CreditNote
                        && !d.CreditedInFull)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        var outcome = BookingChangeOutcome.None;
        foreach (var document in documents)
        {
            if (document.Status == FiscalDocumentStatus.Draft)
                outcome = outcome.Plus(await FollowDraftAsync(document, booking, active, change, ct));
            else if (document.Status == FiscalDocumentStatus.Issued && document.Kind is DocumentKind.Invoice or DocumentKind.Receipt)
                outcome = outcome.Plus(await TellTheHostAsync(booking, document.Id, FiscalDocumentTodos.BookingChangedReason, ct));
        }

        return outcome;
    }

    /// <summary>
    /// A draft follows its booking: made again from it while the booking is active; discarded when the booking bills nothing any more and nothing was collected for it;
    /// kept, with a «To do» for the host, when money was collected (the host decides) or when the draft cannot be made again (its data is missing now).
    /// </summary>
    private async Task<BookingChangeOutcome> FollowDraftAsync(FiscalDocument draft, BookingFacts booking, bool active, BookingChange change, CancellationToken ct)
    {
        if (!active)
        {
            if (await CollectedOfAsync(booking.Id, ct) > 0m)
                return await TellTheHostAsync(booking, draft.Id, FiscalDocumentTodos.BookingChangedReason, ct);

            draft.Status = FiscalDocumentStatus.Discarded;
            draft.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Draft {DocumentId} of booking {BookingId} discarded: the booking is no longer active ({Change})", draft.Id, booking.Id, change);
            return new BookingChangeOutcome(0, 1, 0, 0, 0);
        }

        try
        {
            await RebuildAsync(draft, ct);
            return new BookingChangeOutcome(1, 0, 0, 0, 0);
        }
        catch (DomainException ex)
        {
            // The draft cannot be made again now (the data of the guest or of the issuer is missing): it keeps what it had, and the host is told to look at it.
            logger.LogWarning("Draft {DocumentId} of booking {BookingId} not made again after {Change}: {Code}", draft.Id, booking.Id, change, ex.Code);
            return await TellTheHostAsync(booking, draft.Id, FiscalDocumentTodos.BookingChangedReason, ct);
        }
    }

    /// <summary>The «To do» of a document for the host (no personal data: a code, the document and its property); an item already open is the one raised.</summary>
    private async Task<BookingChangeOutcome> TellTheHostAsync(BookingFacts booking, Guid documentId, string reason, CancellationToken ct)
    {
        if (activities is null)
            return BookingChangeOutcome.None;

        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.FiscalDocument,
                    Action = ActivityAction.ReviewFiscalDocument,
                    EntityType = FiscalDocumentTodos.EntityType,
                    EntityId = documentId,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Reason = reason,
                },
                ct);
            return new BookingChangeOutcome(0, 0, 0, 0, 1);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nobody to tell, and a retry would find the same.
            logger.LogInformation("Document {DocumentId} of booking {BookingId}: property not found, no «To do» raised ({Reason})", documentId, booking.Id, reason);
            return BookingChangeOutcome.None;
        }
    }

    /// <summary>The booking bills a document: a status of the one definition of the code and no no-show mark (<see cref="AlloggiatiStatusRules"/>).</summary>
    private static bool IsActive(BookingFacts booking) =>
        AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;

    /// <summary>The money collected for a booking, net of the refunds.</summary>
    private async Task<decimal> CollectedOfAsync(Guid bookingId, CancellationToken ct) =>
        (await CollectedByBookingAsync(db, [bookingId], ct)).GetValueOrDefault(bookingId);
}
