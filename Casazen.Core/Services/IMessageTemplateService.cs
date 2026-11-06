using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The message templates of a host (E05/F02/US02/T02, NF-63): its own texts, with the variables of <see cref="MessageTemplateRenderer"/>, kept by the
/// organization (<c>MessageTemplates</c>, unique per name and language) and filled in CasaZen with the data of a conversation. The templates of the
/// channel manager are another thing: read only, never stored (<see cref="IOctorateApiClient.GetChatTemplatesAsync"/>).
/// </summary>
/// <remarks>
/// <para><b>Tenant data.</b> A template belongs to the organization of the scope: every method names <see cref="HostScope.OrgId"/> in its queries, on top
/// of the tenant filter, and a template of another organization is a 404 <c>message_template_not_found</c> for read, change, delete and render alike. A
/// template is not bound to a property, so the reach of a collaborator «Solo alcuni» does not narrow the templates; it does narrow the conversation a
/// template is rendered for (<see cref="RenderAsync"/>).</para>
/// <para><b>No personal data in a template.</b> The body holds the host's own words and the names of variables, never a guest's data; the values are
/// read when a template is rendered and are not stored.</para>
/// </remarks>
public interface IMessageTemplateService
{
    /// <summary>The templates of the organization, by name and language; with <paramref name="activeOnly"/> only the ones the composer offers.</summary>
    Task<IReadOnlyList<MessageTemplateItem>> ListAsync(HostScope scope, bool activeOnly, CancellationToken ct = default);

    /// <summary>
    /// Creates a template. 422 <c>message_template_invalid</c> (name, language or body missing or too long) or <c>template_variable_unknown</c> (a
    /// variable that is not one of the eight); 409 <c>message_template_exists</c> for a second template with the same name and language.
    /// </summary>
    Task<MessageTemplateItem> CreateAsync(
        HostScope scope,
        string? name,
        string? language,
        string? body,
        bool isActive,
        CancellationToken ct = default);

    /// <summary>Changes a template of the organization; the same refusals as <see cref="CreateAsync"/>, and 404 <c>message_template_not_found</c>.</summary>
    Task<MessageTemplateItem> UpdateAsync(
        HostScope scope,
        Guid id,
        string? name,
        string? language,
        string? body,
        bool isActive,
        CancellationToken ct = default);

    /// <summary>Deletes a template of the organization. 404 <c>message_template_not_found</c>.</summary>
    Task DeleteAsync(HostScope scope, Guid id, CancellationToken ct = default);

    /// <summary>
    /// The text of a template for a conversation: the variables are filled in with the booking and the property of the conversation (dates as Rome
    /// dates, times on the property's clock). 404 <c>thread_not_found</c> for a conversation out of the reach of the scope; 422
    /// <c>template_variable_missing</c> when the conversation has no value for a variable the template uses (a request with no booking has no dates), never
    /// a literal <c>{{...}}</c> in the text. Nothing is stored and nothing is sent: the host reads the text and sends it as any message.
    /// </summary>
    Task<string> RenderAsync(HostScope scope, Guid id, MessageThreadKey threadKey, CancellationToken ct = default);

    /// <summary>
    /// The templates the channel manager keeps for a conversation of a channel, read only (<see cref="IOctorateApiClient.GetChatTemplatesAsync"/>): the name and
    /// the body to copy into the composer, never edited and never stored. A conversation of the direct kind has none (an empty list, the channel manager is not asked).
    /// 404 <c>thread_not_found</c> for a conversation out of the reach of the scope; the typed errors of the channel manager (the connector off, the chat module
    /// not in the license) as they are.
    /// </summary>
    Task<IReadOnlyList<ChannelTemplateItem>> ChannelTemplatesAsync(HostScope scope, MessageThreadKey threadKey, CancellationToken ct = default);
}

/// <summary>A template of the channel manager, to be copied: its name, the language it is written in (when the channel manager says), and its body.</summary>
public sealed record ChannelTemplateItem(string Name, string? Language, string Text);

/// <summary>A template as the screens list it. <paramref name="Variables"/> are the variables its body uses.</summary>
public sealed record MessageTemplateItem(
    Guid Id,
    string Name,
    string Language,
    string Body,
    bool IsActive,
    IReadOnlyList<string> Variables,
    DateTime UpdatedAt);
