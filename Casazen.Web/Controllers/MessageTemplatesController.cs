using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The helpers of the composer of the hub of messages (E05/F02/US02/T02, NF-63): the host's own templates (<c>api/message-templates</c>), the templates of the
/// channel manager for a conversation (read only), and the translation of a message in both directions. Reading is <c>booking.read</c> and changing a
/// template is <c>booking.write</c>; a template is a datum of the organization (another organization's is a 404), and a conversation is always read in the
/// caller's reach (<see cref="HostScope"/>: a collaborator «Solo alcuni» reaches the conversations of its properties only). The words of the messages are
/// the guest's personal data: they never go in a log, and every answer is private and not cached.
/// </summary>
[ApiController]
[Route("api/message-templates")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class MessageTemplatesController(
    IMessageTemplateService templates,
    IMessageTranslationService translation,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The templates of the organization, by name and language; <c>activeOnly</c> keeps the ones the composer offers. Each has the <c>variables</c> its body
    /// uses.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MessageTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<MessageTemplateDto>>> List([FromQuery] bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var items = await templates.ListAsync(scope, activeOnly, cancellationToken);
        return Ok(items.Select(MessageTemplateDto.From).ToList());
    }

    /// <summary>
    /// Creates a template. 201; 409 <c>message_template_exists</c> (the same name and language); 422 <c>message_template_invalid</c> (name, language or text
    /// missing or too long) or <c>template_variable_unknown</c> (a variable that is none of the eight).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(MessageTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MessageTemplateDto>> Create([FromBody] SaveMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var created = await templates.CreateAsync(scope, request.Name, request.Language, request.Body, request.IsActive, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, MessageTemplateDto.From(created));
    }

    /// <summary>Changes a template of the organization. 404 <c>message_template_not_found</c>; the same 409 and 422 as the creation.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(MessageTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MessageTemplateDto>> Update(Guid id, [FromBody] SaveMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var updated = await templates.UpdateAsync(scope, id, request.Name, request.Language, request.Body, request.IsActive, cancellationToken);
        return Ok(MessageTemplateDto.From(updated));
    }

    /// <summary>Deletes a template of the organization. 204; 404 <c>message_template_not_found</c>.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        await templates.DeleteAsync(scope, id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The text of a template for a conversation (<c>{ threadKey }</c>): the variables are filled in with the booking and the property of the conversation.
    /// Nothing is stored or sent. 400 <c>thread_key_invalid</c>; 404 <c>message_template_not_found</c> or <c>thread_not_found</c>; 422
    /// <c>template_variable_missing</c> (naming the variable the conversation has no value for) or <c>template_variable_unknown</c>.
    /// </summary>
    [HttpPost("{id:guid}/render")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(typeof(RenderedMessageTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RenderedMessageTemplateDto>> Render(Guid id, [FromBody] RenderMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(request.ThreadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        return Ok(new RenderedMessageTemplateDto { Text = await templates.RenderAsync(scope, id, key, cancellationToken) });
    }

    /// <summary>
    /// The templates the channel manager keeps for a conversation of a channel: the name and the body to copy. Read only: never edited, never stored. A
    /// conversation with the guest of a direct booking has none (an empty list). 400 <c>thread_key_invalid</c>; 404 <c>thread_not_found</c>; 409 with the
    /// typed code of the channel manager when it cannot answer (<c>octorate_not_connected</c>, <c>chat_not_licensed</c>).
    /// </summary>
    [HttpGet("~/api/messages/threads/{threadKey}/channel-templates")]
    [ProducesResponseType(typeof(IReadOnlyList<ChannelTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<ChannelTemplateDto>>> ChannelTemplates(string threadKey, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(threadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var items = await templates.ChannelTemplatesAsync(scope, key, cancellationToken);
        return Ok(items.Select(ChannelTemplateDto.From).ToList());
    }

    /// <summary>
    /// The translation of a message of a conversation (<c>messageId</c>) or of a text the host is writing (<c>text</c>), one of the two, into
    /// <c>targetLanguage</c>. It is for the screen: marked <c>machineTranslated</c>, never stored, the message keeps its original. CasaZen's AI first, <b>only
    /// with <c>Features:MessageTranslationAi</c> on (off by default: what a guest wrote goes to an external sub-processor)</b>, the text with its contact data
    /// and the names of the conversation replaced; the channel manager's translation of the message when the AI is off or cannot be used;
    /// 422 <c>translation_unavailable</c> with neither. 400 <c>thread_key_invalid</c>; 404 <c>thread_not_found</c> or <c>message_not_found</c>; 422
    /// <c>translation_language_invalid</c>, <c>translation_input_invalid</c>, <c>message_text_invalid</c>; 429 <c>rate_limited</c> (the limits of the AI).
    /// </summary>
    [HttpPost("~/api/messages/translate")]
    [AiRateLimit]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(MessageTranslationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<MessageTranslationDto>> Translate([FromBody] TranslateMessageRequest request, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(request.ThreadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var translated = await translation.TranslateAsync(scope, key, request.MessageId, request.Text, request.TargetLanguage, cancellationToken);
        return Ok(MessageTranslationDto.From(translated));
    }

    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private ObjectResult InvalidKey() =>
        this.ApiProblem(StatusCodes.Status400BadRequest, MessageThreadKey.InvalidCode, "ThreadKeyInvalid");

    private ObjectResult Forbidden() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
