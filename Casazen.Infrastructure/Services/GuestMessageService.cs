using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestMessageService"/>
/// <remarks>
/// <para>The guest side receives the booking the session proved (<see cref="IGuestSessionService.FindBookingAsync"/>) and reads the
/// tenant of the request, which a guest session binds to the org of the site; the host side reads the tenant of the caller after the
/// controller authorized the booking, and the list also names the org of the scope. Every query of a thread names the booking or the org
/// as well, so the tenant filter is a second wall.</para>
/// <para><b>The text of a message is never in a log</b>: only ids and counters. It is never in an email either.</para>
/// <para><b>The two emails</b> (<see cref="NotifyHostAsync"/>, <see cref="NotifyGuestAsync"/>) leave from the call that wrote the
/// message and from nowhere else; the claim of the ten-minute slot is one conditional <c>UPDATE</c>, so two messages at the same
/// moment queue one email. The message is saved before: a failed email never loses a message.</para>
/// </remarks>
public sealed class GuestMessageService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<GuestMessageService> logger,
    TimeProvider? timeProvider = null) : IGuestMessageService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestThreadView> GetForGuestAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var state = GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, Now());
        if (state == GuestThreadState.Unavailable)
            throw GuestNotFound();

        return new GuestThreadView(
            state,
            GuestMessageRules.ClosesAtUtc(booking.CheckOutDate),
            await ReadMessagesAsync(booking.Id, cancellationToken));
    }

    public async Task<SentGuestMessage> SendAsGuestAsync(
        Booking booking,
        string? text,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var now = Now();
        var state = GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, now);
        if (state == GuestThreadState.Unavailable)
            throw GuestNotFound();
        if (state == GuestThreadState.Closed)
            throw ThreadClosed();

        var (clean, key) = Validate(text, idempotencyKey);
        var stored = GuestMessageRules.Redact(clean, out var redacted);

        var threadId = await EnsureThreadAsync(booking, now, cancellationToken);
        var (message, replayed) = await WriteAsync(threadId, booking.OrgId, GuestMessageSender.Guest, stored, key, null, now, cancellationToken);
        if (replayed)
            return new SentGuestMessage(message, redacted, true);

        await db.GuestThreads
            .Where(t => t.Id == threadId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(t => t.LastMessageAt, t => t.LastMessageAt > now ? t.LastMessageAt : now)
                    .SetProperty(t => t.LastGuestMessageAt, t => t.LastGuestMessageAt > now ? t.LastGuestMessageAt : now),
                cancellationToken);

        logger.LogInformation("Guest message {MessageId} written in thread {ThreadId} of booking {BookingId}", message.Id, threadId, booking.Id);
        await NotifyHostAsync(booking, threadId, now, cancellationToken);
        return new SentGuestMessage(message, redacted, false);
    }

    public async Task<HostThreadView> GetForHostAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.Source, b.Status, b.CheckOutDate })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found");

        var state = GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, Now());
        if (state == GuestThreadState.Unavailable)
            return new HostThreadView(state, null, 0, false, []);

        var thread = await db.GuestThreads
            .AsNoTracking()
            .Where(t => t.BookingId == bookingId)
            .Select(t => new { t.LastGuestMessageAt, t.LastHostMessageAt, t.HostReadAt })
            .FirstOrDefaultAsync(cancellationToken);
        var closesAt = GuestMessageRules.ClosesAtUtc(booking.CheckOutDate);
        if (thread is null)
            return new HostThreadView(state, closesAt, 0, false, []);

        var messages = await ReadMessagesAsync(bookingId, cancellationToken);
        var unread = messages.Count(m => m.Sender == GuestMessageSender.Guest && (thread.HostReadAt is null || m.CreatedAt > thread.HostReadAt));
        return new HostThreadView(
            state,
            closesAt,
            unread,
            state == GuestThreadState.Open && NeedsReply(thread.LastGuestMessageAt, thread.LastHostMessageAt),
            messages);
    }

    public async Task<SentGuestMessage> ReplyAsHostAsync(
        Guid bookingId,
        string? text,
        string? idempotencyKey,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found");

        var now = Now();
        if (GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, now) != GuestThreadState.Open)
            throw ThreadClosed();

        var (clean, key) = Validate(text, idempotencyKey);
        var threadId = await EnsureThreadAsync(booking, now, cancellationToken);
        var (message, replayed) = await WriteAsync(threadId, booking.OrgId, GuestMessageSender.Host, clean, key, userId, now, cancellationToken);
        if (replayed)
            return new SentGuestMessage(message, false, true);

        // Answering is reading: whatever the guest wrote up to now is no longer «unread».
        await db.GuestThreads
            .Where(t => t.Id == threadId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(t => t.LastMessageAt, t => t.LastMessageAt > now ? t.LastMessageAt : now)
                    .SetProperty(t => t.LastHostMessageAt, t => t.LastHostMessageAt > now ? t.LastHostMessageAt : now)
                    .SetProperty(t => t.HostReadAt, t => t.LastGuestMessageAt),
                cancellationToken);

        logger.LogInformation("Host message {MessageId} written in thread {ThreadId} of booking {BookingId}", message.Id, threadId, bookingId);
        await NotifyGuestAsync(booking, threadId, now, cancellationToken);
        return new SentGuestMessage(message, false, false);
    }

    public async Task<HostThreadView> MarkReadAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        await db.GuestThreads
            .Where(t => t.BookingId == bookingId && t.LastGuestMessageAt != null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.HostReadAt, t => t.LastGuestMessageAt), cancellationToken);
        return await GetForHostAsync(bookingId, cancellationToken);
    }

    public async Task<HostThreadPage> ListForHostAsync(
        HostScope scope,
        bool? needsReply,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, GuestMessageRules.MaxPageSize);
        var now = Now();

        // A thread is open while its check-out day is not more than seven days behind today (Europe/Rome): the same rule as StateOf.
        var openFrom = RomeCalendar.TodayAt(new DateTimeOffset(now)).AddDays(-GuestMessageRules.ClosedAfterCheckOutDays);

        var threads = db.GuestThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId
                && t.Booking.Source == BookingSource.Direct
                && (t.Booking.Status == BookingStatus.Confirmed
                    || t.Booking.Status == BookingStatus.CheckedIn
                    || t.Booking.Status == BookingStatus.CheckedOut))
            .InScope(scope);
        if (propertyId is { } id)
            threads = threads.Where(t => t.PropertyId == id);

        if (needsReply == true)
        {
            threads = threads.Where(t => t.LastGuestMessageAt != null
                && (t.LastHostMessageAt == null || t.LastHostMessageAt < t.LastGuestMessageAt)
                && t.Booking.CheckOutDate >= openFrom);
        }
        else if (needsReply == false)
        {
            threads = threads.Where(t => t.LastGuestMessageAt == null
                || (t.LastHostMessageAt != null && t.LastHostMessageAt >= t.LastGuestMessageAt)
                || t.Booking.CheckOutDate < openFrom);
        }

        var total = await threads.CountAsync(cancellationToken);
        var rows = await threads
            .OrderByDescending(t => t.LastMessageAt)
            .ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new ThreadRow(
                t.BookingId,
                t.Booking.BookingCode,
                t.PropertyId,
                t.Property.Name,
                t.Booking.Guest.FirstName,
                t.Booking.Guest.LastName,
                t.Booking.CheckInDate,
                t.Booking.CheckOutDate,
                t.Booking.Status,
                t.LastMessageAt,
                t.LastGuestMessageAt,
                t.LastHostMessageAt,
                db.GuestMessages
                    .Where(m => m.ThreadId == t.Id && m.Sender == GuestMessageSender.Guest)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Text)
                    .FirstOrDefault(),
                db.GuestMessages.Count(m => m.ThreadId == t.Id
                    && m.Sender == GuestMessageSender.Guest
                    && (t.HostReadAt == null || m.CreatedAt > t.HostReadAt))))
            .ToListAsync(cancellationToken);

        var items = rows.Select(r =>
        {
            var state = GuestMessageRules.StateOf(BookingSource.Direct, r.Status, r.CheckOutDate, now);
            var lastIsHost = r.LastHostMessageAt is not null && (r.LastGuestMessageAt is null || r.LastHostMessageAt > r.LastGuestMessageAt);
            return new HostThreadSummary(
                r.BookingId,
                BookingCodes.Format(r.BookingCode),
                r.PropertyId,
                r.PropertyName,
                $"{r.GuestFirstName} {r.GuestLastName}".Trim(),
                r.CheckInDate,
                r.CheckOutDate,
                state,
                r.LastMessageAt,
                r.LastGuestMessageAt is null && r.LastHostMessageAt is null
                    ? null
                    : lastIsHost ? GuestMessageSender.Host : GuestMessageSender.Guest,
                r.LastGuestMessageText is null ? null : GuestMessageRules.Preview(r.LastGuestMessageText),
                r.UnreadCount,
                state == GuestThreadState.Open && NeedsReply(r.LastGuestMessageAt, r.LastHostMessageAt));
        }).ToList();
        return new HostThreadPage(items, total, page, pageSize);
    }

    private static bool NeedsReply(DateTime? lastGuestMessageAt, DateTime? lastHostMessageAt) =>
        lastGuestMessageAt is not null && (lastHostMessageAt is null || lastHostMessageAt < lastGuestMessageAt);

    /// <summary>The latest messages of the thread of a booking, oldest first.</summary>
    private async Task<IReadOnlyList<GuestThreadMessage>> ReadMessagesAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var latest = await db.GuestMessages
            .AsNoTracking()
            .Where(m => m.Thread.BookingId == bookingId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(GuestMessageRules.TranscriptMaxMessages)
            .Select(m => new GuestThreadMessage(m.Id, m.Sender, m.Text, m.CreatedAt))
            .ToListAsync(cancellationToken);
        latest.Reverse();
        return latest;
    }

    /// <summary>The id of the thread of the booking; the first message of either side creates it (two at once: the loser reads the winner's).</summary>
    private async Task<Guid> EnsureThreadAsync(Booking booking, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await FindThreadIdAsync(booking.Id, cancellationToken);
        if (existing is { } found)
            return found;

        var thread = new GuestThread
        {
            OrgId = booking.OrgId,
            PropertyId = booking.PropertyId,
            BookingId = booking.Id,
            CreatedAt = now,
            LastMessageAt = now,
        };
        db.GuestThreads.Add(thread);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var winner = await FindThreadIdAsync(booking.Id, cancellationToken);
            if (winner is null)
                throw;

            return winner.Value;
        }

        return thread.Id;
    }

    private Task<Guid?> FindThreadIdAsync(Guid bookingId, CancellationToken cancellationToken) =>
        db.GuestThreads
            .AsNoTracking()
            .Where(t => t.BookingId == bookingId)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Writes the message, or finds the one the same key wrote before (a retried send): <c>Replayed</c> is then true.</summary>
    private async Task<(GuestThreadMessage Message, bool Replayed)> WriteAsync(
        Guid threadId,
        Guid orgId,
        GuestMessageSender sender,
        string text,
        string key,
        string? userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var earlier = await FindByKeyAsync(threadId, key, cancellationToken);
        if (earlier is not null)
            return (earlier, true);

        var message = new GuestMessage
        {
            OrgId = orgId,
            ThreadId = threadId,
            Sender = sender,
            Text = text,
            CreatedAt = now,
            IdempotencyKey = key,
            AuthorUserId = userId,
        };
        db.GuestMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The same send twice at the same moment: the other call wrote it first.
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(threadId, key, cancellationToken);
            if (winner is null)
                throw;

            return (winner, true);
        }

        return (new GuestThreadMessage(message.Id, message.Sender, message.Text, message.CreatedAt), false);
    }

    private Task<GuestThreadMessage?> FindByKeyAsync(Guid threadId, string key, CancellationToken cancellationToken) =>
        db.GuestMessages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadId && m.IdempotencyKey == key)
            .Select(m => new GuestThreadMessage(m.Id, m.Sender, m.Text, m.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The email to the host: «the guest wrote», without the text, at most one every ten minutes per thread. Best effort after the
    /// message is saved: whatever goes wrong here is logged and never fails the send.
    /// </summary>
    private async Task NotifyHostAsync(Booking booking, Guid threadId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = now.AddMinutes(-GuestMessageRules.NotifyIntervalMinutes);
            var claimed = await db.GuestThreads
                .Where(t => t.Id == threadId && (t.HostNotifiedAt == null || t.HostNotifiedAt <= cutoff))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.HostNotifiedAt, now), cancellationToken);
            if (claimed == 0)
                return;

            var content = EmailTemplates.GuestMessageToHost(
                EmailTemplates.DefaultCulture,
                $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                booking.Property.Name,
                booking.CheckInDate,
                booking.CheckOutDate,
                links.IsConfigured ? links.HostBookingMessages(booking.Id) : null);
            if (!emailQueue.Enqueue(booking.Org.ContactEmail, content, EmailTemplates.Names.GuestMessageToHost))
            {
                // Not queued (no address, provider off): give the slot back, so the next message tries again.
                await db.GuestThreads
                    .Where(t => t.Id == threadId && t.HostNotifiedAt == now)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.HostNotifiedAt, (DateTime?)null), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The email to the host about thread {ThreadId} could not be prepared", threadId);
        }
    }

    /// <summary>The email to the guest: «the host answered», without the text, at most one every ten minutes per thread. Best effort, like the host's.</summary>
    private async Task NotifyGuestAsync(Booking booking, Guid threadId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            // An erased guest keeps a placeholder address that is never deliverable (GuestDataEraser).
            if (booking.Guest.DataAnonymizedDate is not null || string.IsNullOrWhiteSpace(booking.Guest.Email))
                return;

            var cutoff = now.AddMinutes(-GuestMessageRules.NotifyIntervalMinutes);
            var claimed = await db.GuestThreads
                .Where(t => t.Id == threadId && (t.GuestNotifiedAt == null || t.GuestNotifiedAt <= cutoff))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.GuestNotifiedAt, now), cancellationToken);
            if (claimed == 0)
                return;

            var hostName = string.IsNullOrWhiteSpace(booking.Org.DisplayName) ? booking.Property.Name : booking.Org.DisplayName.Trim();
            var content = EmailTemplates.HostReplyToGuest(
                EmailTemplates.DefaultCulture,
                booking.Guest.FirstName,
                hostName,
                booking.Property.Name,
                links.IsConfigured ? links.GuestBookings(booking.Org.Slug, BookingCodes.Format(booking.BookingCode)) : null);
            if (!emailQueue.Enqueue(booking.Guest.Email, content, EmailTemplates.Names.HostReplyToGuest))
            {
                await db.GuestThreads
                    .Where(t => t.Id == threadId && t.GuestNotifiedAt == now)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.GuestNotifiedAt, (DateTime?)null), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The email to the guest about thread {ThreadId} could not be prepared", threadId);
        }
    }

    private static (string Text, string Key) Validate(string? text, string? idempotencyKey)
    {
        var clean = GuestMessageRules.NormalizeText(text);
        if (clean is null || clean.Length > GuestMessageRules.TextMaxLength)
            throw new DomainRuleException("guest_message_text_invalid", "GuestMessageTextInvalid");

        var key = GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey)
            ?? throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");
        return (clean, key);
    }

    private static DomainConflictException ThreadClosed() => new("thread_closed", "GuestThreadClosed");

    /// <summary>The 404 of the guest area: the same for a booking that is not the guest's and for one that has no thread.</summary>
    private static NotFoundException GuestNotFound() =>
        new("No booking of the site matches the booking code and email")
        {
            Code = GuestBookingLookupErrorCodes.NotFound,
            MessageKey = "GuestBookingNotFound",
        };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Now, cut to the microsecond PostgreSQL keeps, so what is written and what is read back compare equal.</summary>
    private DateTime Now()
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    private sealed record ThreadRow(
        Guid BookingId,
        string BookingCode,
        Guid PropertyId,
        string PropertyName,
        string GuestFirstName,
        string GuestLastName,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        BookingStatus Status,
        DateTime LastMessageAt,
        DateTime? LastGuestMessageAt,
        DateTime? LastHostMessageAt,
        string? LastGuestMessageText,
        int UnreadCount);
}
