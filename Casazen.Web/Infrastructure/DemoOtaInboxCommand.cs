using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Offline fixture import, never a transport: uses the normal encrypted EF columns and inbox read model,
/// without recording inbound events, calling Octorate, invoking AI or enqueueing guest notifications.
/// </summary>
public static class DemoOtaInboxCommand
{
    public const string Command = "demo:import-ota-inbox";
    public const string MarkerOrigin = "demo-ota-inbox-import";

    public sealed record Payload(Guid OrgId, List<Conversation> Conversations);
    public sealed record Conversation(Guid BookingId, string ThreadId, string AccommodationId,
        string ReservationId, List<Message> Messages);
    public sealed record Message(string Id, string Text, DateTime SentAt, bool Assistant, string? RuleCode = null);

    public static async Task<int> RunAsync(WebApplication app)
    {
        // Both the deployment identity and actual database schema are checked. No HTTP entry point exists.
        if (!string.Equals(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_NAME"), "test", StringComparison.Ordinal))
            throw new InvalidOperationException("Demo inbox imports require the Railway test environment.");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schema = await db.Database.SqlQueryRaw<string>("select current_schema()::text as \"Value\"").SingleAsync();
        if (schema != "casazen_test")
            throw new InvalidOperationException("Demo inbox imports require the casazen_test schema.");

        var json = app.Configuration["DemoInbox:Payload"]
            ?? throw new InvalidOperationException("A demo inbox payload is required.");
        var payload = JsonSerializer.Deserialize<Payload>(json)
            ?? throw new InvalidOperationException("Invalid demo inbox payload.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await ImportAsync(db, payload, DateTime.UtcNow);
        await transaction.CommitAsync();
        app.Logger.LogInformation("Demo OTA inbox imported: {Threads} conversations, {Messages} messages; no external delivery", result.Threads, result.Messages);
        return 0;
    }

    public static OtaChannel Channel(BookingSource source) => source switch
    {
        BookingSource.Airbnb => OtaChannel.Airbnb,
        BookingSource.BookingCom => OtaChannel.Booking,
        BookingSource.Expedia => OtaChannel.Expedia,
        BookingSource.Vrbo => OtaChannel.Vrbo,
        _ => throw new InvalidOperationException("A demo OTA conversation must belong to a supported OTA booking."),
    };

    public static async Task<(int Threads, int Messages)> ImportAsync(AppDbContext db, Payload payload, DateTime now)
    {
        if (payload.OrgId == Guid.Empty || payload.Conversations.Count is < 1 or > 100
            || payload.Conversations.Select(c => c.BookingId).Distinct().Count() != payload.Conversations.Count
            || payload.Conversations.Select(c => c.ThreadId).Distinct().Count() != payload.Conversations.Count)
            throw new InvalidOperationException("Invalid demo conversation set.");

        var bookingIds = payload.Conversations.Select(c => c.BookingId).ToArray();
        var bookings = await db.Bookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrgId == payload.OrgId && bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var marked = await db.TestDataMarkers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrgId == payload.OrgId && m.EntityType == "Booking" && bookingIds.Contains(m.EntityId))
            .Select(m => m.EntityId).Distinct().ToArrayAsync();
        if (bookings.Count != bookingIds.Length || marked.Length != bookingIds.Length)
            throw new InvalidOperationException("Every booking must belong to this tenant and already carry a test-data marker.");

        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in payload.Conversations)
        {
            _ = Channel(bookings[conversation.BookingId].Source);
            if (!DemoId(conversation.ThreadId) || !DemoId(conversation.AccommodationId) || !DemoId(conversation.ReservationId)
                || conversation.Messages.Count is < 2 or > 40)
                throw new InvalidOperationException("Invalid demo external identifiers or message count.");
            DateTime? previous = null;
            var expectAssistant = false;
            foreach (var message in conversation.Messages)
            {
                if (!DemoId(message.Id) || !messageIds.Add(message.Id) || string.IsNullOrWhiteSpace(message.Text)
                    || message.Text.Length > 2000 || message.SentAt.Kind != DateTimeKind.Utc
                    || message.SentAt > now || (previous.HasValue && message.SentAt <= previous.Value)
                    || message.RuleCode?.Length > OtaMessage.RuleCodeMaxLength || message.Assistant != expectAssistant)
                    throw new InvalidOperationException("Invalid demo message or chronology.");
                previous = message.SentAt;
                expectAssistant = !expectAssistant;
            }
        }

        var addedThreads = 0;
        var addedMessages = 0;
        foreach (var conversation in payload.Conversations)
        {
            var existing = await db.OtaMessageThreads.IgnoreQueryFilters()
                .SingleOrDefaultAsync(t => t.OrgId == payload.OrgId && t.OctorateThreadId == conversation.ThreadId);
            if (existing is not null)
            {
                var ours = await db.TestDataMarkers.IgnoreQueryFilters().AnyAsync(m => m.OrgId == payload.OrgId
                    && m.EntityId == existing.Id && m.EntityType == "OtaMessageThread" && m.Origin == MarkerOrigin);
                if (!ours || existing.BookingId != conversation.BookingId)
                    throw new InvalidOperationException("A demo identifier collides with an existing conversation.");
                continue; // A replay never resets read marks or overwrites a conversation the host has used.
            }
            var booking = bookings[conversation.BookingId];
            var name = await db.Guests.IgnoreQueryFilters().Where(g => g.OrgId == payload.OrgId && g.Id == booking.GuestId)
                .Select(g => g.FirstName).SingleAsync();
            var last = conversation.Messages[^1];
            var thread = new OtaMessageThread
            {
                OrgId = payload.OrgId, PropertyId = booking.PropertyId, BookingId = booking.Id,
                OctorateThreadId = conversation.ThreadId, OctorateAccommodationId = conversation.AccommodationId,
                OctorateReservationId = conversation.ReservationId, ChannelSource = Channel(booking.Source),
                Kind = OtaThreadKind.Reservation, GuestDisplayName = name, AiAssistantOn = false,
                LastMessageAt = last.SentAt,
                LastMessageDirection = last.Assistant ? OtaMessageDirection.Outbound : OtaMessageDirection.Inbound,
                UnreadCount = last.Assistant ? 0 : 1, CreatedAt = now, UpdatedAt = now,
            };
            db.OtaMessageThreads.Add(thread);
            Mark(db, payload.OrgId, thread.Id, "OtaMessageThread", "synthetic-api-snapshot", now);
            foreach (var message in conversation.Messages)
            {
                var row = new OtaMessage
                {
                    OrgId = payload.OrgId, ThreadId = thread.Id, OctorateMessageId = message.Id,
                    Direction = message.Assistant ? OtaMessageDirection.Outbound : OtaMessageDirection.Inbound,
                    SenderName = message.Assistant ? "Team Quokka360" : name, Text = message.Text,
                    SentAt = message.SentAt,
                    ReadAt = message.Assistant || ReferenceEquals(message, last) ? null : conversation.Messages.First(m => m.SentAt > message.SentAt).SentAt,
                    DeliveryState = OtaMessageDeliveryState.Sent,
                    Origin = message.Assistant ? MessageOrigin.Assistant : MessageOrigin.Guest,
                    RuleCode = message.Assistant ? message.RuleCode : null, CreatedAt = now,
                };
                db.OtaMessages.Add(row);
                Mark(db, payload.OrgId, row.Id, "OtaMessage", message.Assistant ? "assistant-simulation-no-provider-run" : "synthetic-api-inbound", now);
                addedMessages++;
            }
            addedThreads++;
        }
        await db.SaveChangesAsync();
        // Read through the same converter as the portal; a wrong key ring aborts the transaction.
        db.ChangeTracker.Clear();
        var externalIds = messageIds.ToArray();
        var stored = await db.OtaMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrgId == payload.OrgId && externalIds.Contains(m.OctorateMessageId!)).ToListAsync();
        if (stored.Count != externalIds.Length || stored.Any(m => string.IsNullOrWhiteSpace(m.Text)))
            throw new InvalidOperationException("Demo inbox read-back failed.");
        return (addedThreads, addedMessages);
    }

    private static bool DemoId(string value) => value.Length is >= 12 and <= 64
        && value.StartsWith("990", StringComparison.Ordinal) && value.All(char.IsAsciiDigit);

    private static void Mark(AppDbContext db, Guid orgId, Guid id, string type, string template, DateTime now) =>
        db.TestDataMarkers.Add(new TestDataMarker
        {
            OrgId = orgId, EntityId = id, EntityType = type, Origin = MarkerOrigin, TemplateKey = template, CreatedAt = now,
        });
}
