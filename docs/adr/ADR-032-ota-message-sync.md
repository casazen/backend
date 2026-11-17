# ADR-032: OTA Message Sync

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-61 (unified hub), NF-62 (thread reply), NF-63 (AI drafts)

## Context

Hosts receive guest messages from multiple OTAs (Airbnb, Booking.com, Expedia) via Octorate. CasaZen needs a unified messages hub to view and reply without switching between OTA extranets.

**Existing code:**
- No OTA message integration exists (scout: NF-61 MISSING, NF-62 MISSING)
- Mobile messages hub completely missing (major gap)
- Push notification routing exists

**Repo scope:** Backend (message sync, thread storage), Frontend (messages hub), Mobile (messages screen, reply composer)

## Decision

### Message Thread Entity

Per Q-A30: one thread per Octorate threadId, separate from CasaZen direct messages.

```csharp
public class OtaMessageThread : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? BookingId { get; set; }
    public string OctorateThreadId { get; set; }
    public OtaChannel ChannelSource { get; set; }       // Airbnb, Booking, Expedia
    public DateTime LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
}

public class OtaMessage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid ThreadId { get; set; }
    public MessageDirection Direction { get; set; }     // Inbound, Outbound
    public string SenderName { get; set; }
    public string MessageText { get; set; }             // Encrypted if PII
    public string? AttachmentUrl { get; set; }          // Link to Octorate URL
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public enum MessageDirection { Inbound, Outbound }
public enum OtaChannel { Airbnb, Booking, Expedia }
```

### Attachment Storage

Per Q-A29: link to Octorate URL, no local copy.

```csharp
// Frontend proxies attachment access
[HttpGet("/api/messages/{messageId}/attachment")]
public async Task<IActionResult> GetAttachment(Guid messageId)
{
    var message = await _db.OtaMessages.FindAsync(messageId);
    
    // Verify user access to booking
    if (!await _authService.CanAccessPropertyAsync(message.Thread.PropertyId))
        return NotFound();
        
    // Proxy fetch from Octorate (never expose Octorate URL directly)
    var stream = await _httpClient.GetStreamAsync(message.AttachmentUrl);
    return File(stream, "application/octet-stream");
}
```

### Message Sync Job

```csharp
public class OtaMessageSyncJob
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var connections = await _db.OctorateConnections.Where(c => c.Status == Connected).ToArrayAsync(ct);
        
        foreach (var conn in connections)
        {
            var since = conn.LastMessageSyncAt ?? DateTime.UtcNow.AddDays(-7);
            var threads = await _octorate.GetMessagesAsync(since, ct);
            
            foreach (var thread in threads)
            {
                var existing = await _db.OtaMessageThreads
                    .Where(t => t.OctorateThreadId == thread.ThreadId)
                    .SingleOrDefaultAsync(ct);
                    
                if (existing == null)
                {
                    existing = new OtaMessageThread
                    {
                        OrgId = conn.OrgId,
                        OctorateThreadId = thread.ThreadId,
                        ChannelSource = MapChannel(thread.Channel),
                        PropertyId = await ResolvePropertyAsync(thread.PropertyRef, ct)
                    };
                    _db.OtaMessageThreads.Add(existing);
                }
                
                foreach (var msg in thread.Messages)
                {
                    if (await _db.OtaMessages.AnyAsync(m => m.OctorateMessageId == msg.Id, ct))
                        continue;  // Already synced
                        
                    _db.OtaMessages.Add(new OtaMessage
                    {
                        ThreadId = existing.Id,
                        OrgId = conn.OrgId,
                        Direction = msg.Direction,
                        SenderName = msg.SenderName,
                        MessageText = _encryption.EncryptIfPii(msg.Text),
                        AttachmentUrl = msg.AttachmentUrl,
                        SentAt = msg.SentAt
                    });
                }
                
                existing.LastMessageAt = thread.Messages.Max(m => m.SentAt);
            }
            
            conn.LastMessageSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

Hangfire recurring every 15 minutes (matches alert delivery cadence).

### Reply via Octorate

```csharp
public async Task ReplyAsync(Guid threadId, string messageText, CancellationToken ct)
{
    var thread = await _db.OtaMessageThreads.FindAsync(threadId, ct);
    
    await _octorate.SendMessageAsync(thread.OctorateThreadId, messageText, ct);
    
    _db.OtaMessages.Add(new OtaMessage
    {
        ThreadId = threadId,
        OrgId = thread.OrgId,
        Direction = MessageDirection.Outbound,
        SenderName = _user.DisplayName,
        MessageText = messageText,
        SentAt = DateTime.UtcNow
    });
    
    await _db.SaveChangesAsync(ct);
}
```

### Unified Hub Display

```typescript
// GET /api/messages?includeOta=true returns both OTA and direct threads
function MessagesHub() {
    const { data: threads } = useQuery({
        queryKey: ['messages', { includeOta: true }],
        queryFn: () => api.getMessages({ includeOta: true })
    });
    
    return (
        <div className="grid grid-cols-3">
            <ThreadList threads={threads} />
            <ThreadDetail />
            <BookingCard />  {/* Booking context beside conversation */}
        </div>
    );
}
```

### Rejected Alternatives

- **Store attachment copies:** Duplicates storage; link to Octorate URL is sufficient per Q-A29
- **Merge OTA and direct threads per booking:** Complex sync logic; separate entities per Q-A30 is cleaner
- **Real-time message sync:** WebSocket from Octorate not available; 15-minute polling acceptable

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-032-R1 | P0 | `OtaMessageThread` table (OrgId, PropertyId, BookingId nullable, OctorateThreadId unique, ChannelSource enum, LastMessageAt, UnreadCount, ITenantOwned) |
| ADR-032-R2 | P0 | `OtaMessage` table (OrgId, ThreadId, Direction enum, SenderName, MessageText, AttachmentUrl nullable, SentAt, ReadAt nullable, ITenantOwned) |
| ADR-032-R3 | P0 | Message text MUST be encrypted if contains PII (guest message = PII); use `EncryptionService` with purpose `Casazen.Message.Content` |
| ADR-032-R4 | P0 | `OtaMessageSyncJob` (Hangfire every 15 minutes) MUST call Octorate messages API, upsert threads and messages, respect rate limit (ADR-023) |
| ADR-032-R5 | P0 | Attachment URL MUST NOT be exposed to client; proxy via `GET /api/messages/{id}/attachment` with property access check |
| ADR-032-R6 | P0 | Messages hub `GET /api/messages?includeOta=true` MUST return unified list of OTA and direct threads, filterable by property/channel/unread |
| ADR-032-R7 | P0 | Reply endpoint `POST /api/messages/threads/{id}/reply` MUST call Octorate `SendMessageAsync` and store outbound message |
| ADR-032-R8 | P0 | Thread detail MUST show booking card (ID, guest name, check-in date, property) beside conversation |
| ADR-032-R9 | P0 | Mobile app MUST implement messages hub at `app/(tabs)/messages.tsx` with conversations list and thread view |
| ADR-032-R10 | P0 | Mobile thread view MUST show booking card in collapsible header (horizontal space constraint) |
| ADR-032-R11 | P0 | Mobile reply composer MUST support text input and AI draft approval button |
| ADR-032-R12 | P0 | Mobile push notification for new message MUST route to thread view on tap |
| ADR-032-R13 | P1 | GDPR deletion `DELETE /api/crm/guests/{id}` MUST anonymize messages where SenderName = guest.Name |
| ADR-032-R14 | P1 | Mobile conversations list MUST show unread badge per thread, "New" indicator for messages in last 24h |

## Consequences

**Follow-on work:**
- Backend: `OtaMessageThread` and `OtaMessage` entities, sync job, reply endpoint, attachment proxy
- Frontend: Messages hub with three-column layout, thread list, thread detail, booking card
- Mobile: Messages screen, thread view, reply composer, push routing
- Tests: Message encryption proof test, GDPR deletion integration test

**Risks:**
- Message sync lag: up to 15 minutes for new OTA messages; document in help
- Attachment URL expiry: if Octorate URLs expire, show "Attachment no longer available" (not error)
- Message encryption overhead: acceptable for PII protection

**CI cost:** +10s for encryption and GDPR tests
