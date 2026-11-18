using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed class StorefrontChatThread : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Property Property { get; set; } = null!;
    public string AccessHash { get; set; } = string.Empty;
    public string VisitorName { get; set; } = string.Empty;
    public string VisitorEmail { get; set; } = string.Empty;
    public DateTime LastMessageAt { get; set; }
    public bool NeedsReply { get; set; }
    public int UnreadCount { get; set; }
    public List<StorefrontChatMessage> Messages { get; set; } = [];
}

public sealed class StorefrontChatMessage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid ThreadId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public OtaMessageDirection Direction { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
