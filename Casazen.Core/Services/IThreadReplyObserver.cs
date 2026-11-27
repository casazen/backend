namespace Casazen.Core.Services;

/// <summary>
/// Told when a person answered a conversation of a guest (E05/F05/US02/T01): <c>MessageReplyService</c> (a conversation of a channel) and <c>GuestMessageService.ReplyAsHostAsync</c> (the
/// conversation of a direct booking) call every registered observer after a reply that went out and that a person wrote or approved. A message the assistant sends by itself (no person
/// behind it) does not count: an escalated message waits for a person. The implementation of the assistant, <c>AssistantEscalationCloser</c>, closes the «To do» of the messages it escalated
/// in that conversation, so a «To do» never outlives the answer.
/// </summary>
/// <remarks>
/// The reply is already sent when an observer runs: an observer that fails is logged and never fails the reply (the «To do» then stays in the list, for the person to close). It works for
/// <paramref name="orgId"/> only.
/// </remarks>
public interface IThreadReplyObserver
{
    /// <summary>A person answered the conversation <paramref name="threadKey"/> of <paramref name="orgId"/>.</summary>
    Task OnHostReplyAsync(Guid orgId, MessageThreadKey threadKey, CancellationToken ct);
}
