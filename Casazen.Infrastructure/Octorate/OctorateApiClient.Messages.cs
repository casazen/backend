using System.Globalization;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The write side of the chat, the reads of the templates and of the translation, and the contact of the booker of a reservation (E05/F02/US02,
/// ADR-032, ADR-023): the operations of <see cref="IOctorateApiClient"/> the messages hub needs to answer. A partial of the client so that the
/// operations added by the tasks of other epics never collide with these.
/// </summary>
public sealed partial class OctorateApiClient
{
    /// <summary>Longest template body kept (a template longer than a message can be is cut off by the composer anyway).</summary>
    internal const int MaxTemplateTextLength = 10_000;

    public async Task<OctorateChatSendResult> SendChatMessageAsync(string accommodationId, string threadId, string text, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var thread = CheckedThreadId(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // The message names the person who writes and the thread on the channel (both required by the OpenAPI); they are read from the thread
        // first. A failure of this read leaves nothing written: the message was not sent, and the caller is told so (it is not the outcome
        // nobody knows of the POST below, which is an OctorateServerException).
        OctorateChatThread remote;
        try
        {
            remote = await GetChatThreadAsync(accommodation, thread, ct);
        }
        catch (OctorateServerException ex)
        {
            throw new OctorateChatNotReadException(ex.OctorateStatusCode, ex);
        }

        if (OctorateChatRequests.NewMessageBody(thread, remote, text, timeProvider.GetUtcNow().UtcDateTime) is not { } json)
            throw new OctorateChatNotSendableException();

        // One attempt, never repeated here (OCT-13): this operation has no idempotency key. SendAsync retries only what Octorate refused before
        // doing anything (a 429, a token it no longer accepts); a timeout or a 5xx is the typed error and the caller decides after the reconciliation.
        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address($"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/messages"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "chat-send",
            ct,
            mapError: ChatErrorOf);

        // The words of the message are in the request, never in anything logged. An answer without an id is not a success: the message may exist.
        using var document = ParseJson(answer);
        if (OctorateChatReader.ReadSendResult(Unwrap(document.RootElement)) is not { } sent)
        {
            logger.LogWarning(
                "Octorate chat-send for org {OrgId}: HTTP {StatusCode} without a message id, the outcome is not known", orgScope.OrgId, answer.StatusCode);
            throw new OctorateServerException(answer.StatusCode);
        }

        return sent;
    }

    public async Task<OctorateGuestContactInfo> GetReservationContactInfoAsync(string accommodationId, string refer, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reference = CheckedId(refer, nameof(refer));

        OctorateAnswer answer;
        try
        {
            // The operation has no body (OpenAPI); every read is recorded by Octorate (CONTACT_INFO_SHOWN).
            answer = await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Post,
                    Address($"/rest/v2/reservation/{Uri.EscapeDataString(accommodation)}/{Uri.EscapeDataString(reference)}/contactInfo")),
                "reservation-contact-info",
                ct);
        }
        catch (OctorateAuthException ex) when (!ex.IsNotConnected)
        {
            // A 401 or a 403 that is not a stale token: the static IP addresses of CasaZen are not registered with Octorate, or the application has no
            // contactInfo permission. Never turned into a booker with no contact.
            logger.LogWarning(
                "Octorate reservation-contact-info for org {OrgId}: refused (HTTP {StatusCode}); the sensitive data is not released to this application",
                orgScope.OrgId,
                ex.OctorateStatusCode);
            throw new OctorateSensitiveDataNotAllowedException(ex.OctorateStatusCode);
        }

        // The answer is the booker's contact data: it is read and returned, never logged (not even when it cannot be parsed).
        using var document = ParseJson(answer);
        var root = Unwrap(document.RootElement);
        return root.ValueKind == JsonValueKind.Object
            ? ReadGuestContactInfo(root)
            : new OctorateGuestContactInfo(null, null, Recognized: false);
    }

    public async Task<OctorateChatTemplate[]> GetChatTemplatesAsync(string accommodationId, string threadId, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var thread = CheckedThreadId(threadId);
        using var document = await GetChatJsonAsync(
            $"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/threads/{Uri.EscapeDataString(thread)}/templates", "chat-templates", ct);

        // An answer that is not a list is one CasaZen cannot read, not a thread with no template.
        return OctorateChatReader.ReadTemplates(document.RootElement) ?? throw new OctorateServerException(null);
    }

    public async Task<string> TranslateChatMessageAsync(string accommodationId, string threadId, string messageId, string language, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var thread = CheckedThreadId(threadId);
        var message = CheckedMessageId(messageId);
        var target = CheckedLanguage(language);

        var answer = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Patch,
                Address($"/rest/v1/chat/{Uri.EscapeDataString(accommodation)}/threads/{Uri.EscapeDataString(thread)}/messages/{Uri.EscapeDataString(message)}/translate/{target}")),
            "chat-translate",
            ct,
            mapError: ChatErrorOf);

        // The translation is read from the answer and returned; an answer that holds none in that language is not a translation.
        using var document = ParseJson(answer);
        return OctorateChatReader.ReadTranslation(Unwrap(document.RootElement), target) ?? throw new OctorateServerException(answer.StatusCode);
    }

    /// <summary>The id of a message, trimmed: a number of 1 to <see cref="MaxConnectionIdDigits"/> digits, as the specification declares it (<c>int64</c>).</summary>
    internal static string CheckedMessageId(string messageId)
    {
        var id = messageId?.Trim();
        if (!IsNumericId(id))
            throw new ArgumentException($"A message id is a number of 1 to {MaxConnectionIdDigits} digits.", nameof(messageId));

        return id!;
    }

    /// <summary>The language of a translation as the channel manager spells it (<c>EN</c>): one of the 14 it has. Anything else is refused before a call leaves.</summary>
    internal static string CheckedLanguage(string language)
    {
        var code = language?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || !LanguageMapper.OctorateLanguages.Contains(code, StringComparer.Ordinal))
            throw new ArgumentException("Not a language of the channel manager.", nameof(language));

        return code;
    }
}

/// <summary>The bodies of the chat requests CasaZen makes (E05/F02/US02/T01), built apart from the client so that they can be read and tested.</summary>
internal static class OctorateChatRequests
{
    /// <summary>The way a message goes, toward the channel (<c>OTA_FLOW_DIRECTION</c> = <c>TO</c>).</summary>
    internal const string ToChannel = "TO";

    /// <summary>The processor of a message for a channel.</summary>
    internal const string ChannelProcessor = "CHANNEL";

    /// <summary>
    /// The body of a message to the guest of <paramref name="thread"/> (<c>ChatMessageDTO</c>): the thread, its id on the channel, the person of the host
    /// side who writes, the processor <c>CHANNEL</c>, the state <c>CREATED</c> (Octorate sends it and reports the next state), the instant, and the
    /// attributes <c>TEXT</c> and <c>OTA_FLOW_DIRECTION</c> = <c>TO</c>. <c>null</c> when the thread names no person of the host side or no id on the
    /// channel, or when an id is not the number the specification declares: the message cannot be built and nothing is sent.
    /// </summary>
    internal static string? NewMessageBody(string threadId, OctorateChatThread thread, string text, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.ExternalThreadId is not { } externalThreadId
            || thread.HostPersonId is not { } personId
            || !long.TryParse(threadId, NumberStyles.None, CultureInfo.InvariantCulture, out var threadNumber)
            || !long.TryParse(personId, NumberStyles.None, CultureInfo.InvariantCulture, out var personNumber))
        {
            return null;
        }

        var body = new Dictionary<string, object?>
        {
            ["threadId"] = threadNumber,
            ["externalThreadId"] = externalThreadId,
            ["processor"] = ChannelProcessor,
            ["status"] = "CREATED",
            ["createTime"] = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["sender"] = new Dictionary<string, object?> { ["id"] = personNumber },
            ["attributes"] = new[]
            {
                new Dictionary<string, object?> { ["type"] = OctorateChatReader.TextAttribute, ["value"] = text },
                new Dictionary<string, object?> { ["type"] = OctorateChatReader.FlowDirectionAttribute, ["value"] = ToChannel },
            },
        };
        return JsonSerializer.Serialize(body);
    }
}
