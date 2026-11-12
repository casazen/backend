using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Reads the answers of the chat operations of Octorate (E05/F01/US01/T02): the threads (<c>ChatThreadDTO</c>) and the messages
/// (<c>ChatMessageDTO</c>, in a bare list or in the <c>{page, data}</c> wrapper of <c>ApiListResponse</c>). The names are those of the OpenAPI
/// of 2026-10-31: the sandbox has no <c>chat</c> module in its license (the chat reads answer 403, <c>recentMessages</c> 500), so none of them has
/// been seen on the wire yet and a member that is not there is <c>null</c>, never a made-up value.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Text.</b> The words of a message are the attribute <c>TEXT</c> (<c>attributes[].type</c> / <c>value</c>).</item>
/// <item><b>Direction.</b> Octorate marks the messages it sends to a channel with the attribute <c>OTA_FLOW_DIRECTION</c> = <c>TO</c> (and
/// <c>FROM</c> for the ones that come from it). Without it the sender decides, as the specification describes the person: a user (<c>userId</c>)
/// is the host side, a person with an <c>externalGuest</c> ("set it up if coming from the guest, leave null if coming from the property") or a
/// <c>guestId</c> is the guest, and so is the <c>externalSender</c> of a message. A message that none of these places is the host's: it is
/// never taken for a message of the guest, which would raise an event and an unread message for something the guest never wrote.</item>
/// <item><b>Delivery state.</b> <c>CREATED</c>, <c>WAIT_AUTH</c>, <c>SENT</c>, <c>ERROR</c>, <c>MANUAL_VERIFY</c>. A word that is none of them is, for an
/// outbound message, <see cref="OtaMessageDeliveryState.ManualVerify"/>: a success that Octorate did not state is never assumed.</item>
/// <item><b>Attachment.</b> The address and the kind (<c>JPEG</c>, <c>PNG</c>, <c>OCTET_STREAM</c>) as given, only when the address is an absolute
/// http or https one: no copy of the file is made.</item>
/// <item>Nothing here logs: the values are personal data.</item>
/// </list>
/// </remarks>
internal static class OctorateChatReader
{
    /// <summary>The attribute of a message that holds its words.</summary>
    internal const string TextAttribute = "TEXT";

    /// <summary>The attribute of a message that says which way it goes: <c>TO</c> the channel, or <c>FROM</c> it.</summary>
    internal const string FlowDirectionAttribute = "OTA_FLOW_DIRECTION";

    /// <summary>The attribute of a thread that carries the details of an Airbnb booking.</summary>
    internal const string AirbnbBookingDetailsAttribute = "AIRBNB_BOOKING_DETAILS";

    /// <summary>The processor of an internal note of the team, which is not part of the conversation with the guest.</summary>
    internal const string NoteProcessor = "NOTE";

    /// <summary>The attribute of a message that holds its translations: a map from a language to its text (E05/F02/US02/T02).</summary>
    internal const string TranslationMapAttribute = "TRANSLATION_MAP";

    private const int TemplateNameMaxLength = 200;

    private const int ExternalThreadIdMaxLength = 200;

    /// <summary>
    /// The page of threads an answer holds: a bare list (v1) or the array of a wrapper. <c>null</c> when the answer is neither, so a caller can
    /// tell an empty list from an answer it cannot read. An item without a usable id is left out but counted.
    /// </summary>
    internal static OctorateChatThreadPage? ReadThreadPage(JsonElement root)
    {
        if (OctorateReservationReader.ListItems(root) is not { } items)
            return null;

        var listed = items.ToList();
        return new OctorateChatThreadPage([.. listed.Select(item => ReadThread(item)).OfType<OctorateChatThread>()], listed.Count);
    }

    /// <summary>The thread of <paramref name="item"/>, or <c>null</c> when it is not an object or has no usable id.</summary>
    /// <param name="item">One thread of an answer.</param>
    /// <param name="fallbackId">The id the call asked for, used when the answer does not repeat it.</param>
    internal static OctorateChatThread? ReadThread(JsonElement item, string? fallbackId = null)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateJson.Text(item, "id") ?? fallbackId;
        if (id is null || id.Length > OtaMessageThread.OctorateIdMaxLength)
            return null;

        var reservation = OctorateJson.Find(item, "reservation") is { ValueKind: JsonValueKind.Object } found ? found : default;
        return new OctorateChatThread
        {
            Id = id,
            AccommodationId = OctorateJson.Text(item, "accommodationId"),
            DefaultProcessor = OctorateJson.Text(item, "defaultProcessor"),
            AiDriven = OctorateJson.Bool(item, "aiDriven"),
            ReservationId = OctorateJson.Text(reservation, "id", "reservationId"),
            ReservationChannelId = OctorateJson.Text(reservation, "channelId"),
            ReservationChannelName = OctorateJson.Text(reservation, "channelName"),
            ConnectionIds = ConnectionIdsOf(item),
            HasAirbnbBookingDetails = HasAttribute(item, AirbnbBookingDetailsAttribute),
            GuestName = GuestNameOf(item),
            ArchivedAt = OctorateJson.Instant(item, "archiveTime"),
            LastActivityAt = LastActivityOf(item),
            ExternalThreadId = ExternalThreadIdOf(item),
            HostPersonId = HostPersonIdOf(item),
        };
    }

    /// <summary>
    /// What the channel manager answered to a message sent to a channel (<c>ChatMessageDTO</c>): its id and the state it reports. <c>null</c> when the
    /// answer is not an object or has no usable id: the message may exist all the same, so it is never read as a success. A <c>status</c> that is
    /// missing or none of the five words is <see cref="OtaMessageDeliveryState.ManualVerify"/> (a success Octorate did not state is never assumed).
    /// </summary>
    internal static OctorateChatSendResult? ReadSendResult(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateJson.Text(message, "id");
        if (id is null || id.Length > OtaMessage.OctorateMessageIdMaxLength)
            return null;

        return new OctorateChatSendResult(id, DeliveryStateOf(OctorateJson.Text(message, "status"), OtaMessageDirection.Outbound));
    }

    /// <summary>
    /// The templates an answer holds (<c>MailTemplateDTO</c>: <c>id</c>, <c>name</c>, <c>language</c>, <c>text</c>): a bare list or the array of a
    /// wrapper. <c>null</c> when the answer is not a list, so a caller can tell an empty list from an answer it cannot read. An item without an id or
    /// without a body is left out.
    /// </summary>
    internal static OctorateChatTemplate[]? ReadTemplates(JsonElement root)
    {
        if (OctorateReservationReader.ListItems(root) is not { } items)
            return null;

        var templates = new List<OctorateChatTemplate>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = OctorateJson.Text(item, "id");
            var text = OctorateJson.Find(item, "text") is { ValueKind: JsonValueKind.String } body ? body.GetString()?.Trim() : null;
            if (id is null || id.Length > OtaMessage.OctorateMessageIdMaxLength || string.IsNullOrEmpty(text))
                continue;

            templates.Add(new OctorateChatTemplate(
                id,
                Clip(OctorateJson.Text(item, "name") ?? id, TemplateNameMaxLength),
                OctorateJson.Text(item, "language")?.ToUpperInvariant(),
                Clip(text, OctorateApiClient.MaxTemplateTextLength)));
        }

        return [.. templates];
    }

    /// <summary>
    /// The translation of a message in <paramref name="language"/> (<c>IT</c>, <c>EN</c>, ...), from the attribute <c>TRANSLATION_MAP</c> of the message
    /// Octorate answers: a JSON object that maps a language to its text. <c>null</c> when the answer has no such attribute, it is not that object, or it
    /// holds nothing in that language: never a guess.
    /// </summary>
    internal static string? ReadTranslation(JsonElement message, string language)
    {
        if (AttributeOf(message, TranslationMapAttribute) is not { } map)
            return null;

        try
        {
            using var document = JsonDocument.Parse(map);
            return document.RootElement.ValueKind == JsonValueKind.Object ? OctorateJson.Text(document.RootElement, language) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>The id of the thread on the channel: the first <c>externalId</c> of <c>chatThreadExternals</c>.</summary>
    private static string? ExternalThreadIdOf(JsonElement thread)
    {
        if (OctorateJson.Find(thread, "chatThreadExternals") is not { ValueKind: JsonValueKind.Array } externals)
            return null;

        foreach (var external in externals.EnumerateArray())
        {
            if (OctorateJson.Text(external, "externalId") is { Length: <= ExternalThreadIdMaxLength } id)
                return id;
        }

        return null;
    }

    /// <summary>The first member of the thread that is a user of the channel manager (it has a <c>userId</c>): the id of the person the host's messages are written as.</summary>
    private static string? HostPersonIdOf(JsonElement thread)
    {
        if (OctorateJson.Find(thread, "persons") is not { ValueKind: JsonValueKind.Array } persons)
            return null;

        foreach (var person in persons.EnumerateArray())
        {
            if (person.ValueKind == JsonValueKind.Object
                && OctorateJson.Text(person, "userId") is not null
                && OctorateJson.Text(person, "id") is { Length: <= 19 } id
                && id.All(char.IsAsciiDigit))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// The messages an answer holds, with the number of items it had and the number of pages it says there are (<c>page.totalPages</c>, when
    /// the answer is the wrapper). <c>null</c> when the answer is not a list.
    /// </summary>
    internal static ChatMessagePage? ReadMessagePage(JsonElement root)
    {
        if (OctorateReservationReader.ListItems(root) is not { } items)
            return null;

        var listed = items.ToList();
        int? totalPages = null;
        if (root.ValueKind == JsonValueKind.Object && OctorateJson.Find(root, "page") is { ValueKind: JsonValueKind.Object } page)
            totalPages = OctorateJson.Int(page, "totalPages");

        return new ChatMessagePage([.. listed.Select(ReadMessage).OfType<OctorateChatMessage>()], listed.Count, totalPages);
    }

    /// <summary>The message of <paramref name="item"/>, or <c>null</c> when it is not an object or has no usable id or time.</summary>
    internal static OctorateChatMessage? ReadMessage(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = OctorateJson.Text(item, "id");
        if (id is null || id.Length > OtaMessage.OctorateMessageIdMaxLength)
            return null;

        var sentAt = OctorateJson.Instant(item, "sentTime") ?? OctorateJson.Instant(item, "createTime");
        if (sentAt is null)
            return null;

        var sender = OctorateJson.Find(item, "sender") is { ValueKind: JsonValueKind.Object } s ? s : default;
        var externalSender = OctorateJson.Find(item, "externalSender") is { ValueKind: JsonValueKind.Object } e ? e : default;
        var direction = DirectionOf(item, sender, externalSender);
        var (attachmentUrl, attachmentContentType) = AttachmentOf(item);

        return new OctorateChatMessage
        {
            Id = id,
            ThreadId = OctorateJson.Text(item, "threadId"),
            Processor = OctorateJson.Text(item, "processor"),
            Direction = direction,
            SenderName = NameOf(sender) ?? string.Empty,
            Text = AttributeOf(item, TextAttribute) ?? string.Empty,
            AttachmentUrl = attachmentUrl,
            AttachmentContentType = attachmentContentType,
            SentAt = sentAt.Value,
            ReadAt = ReadAtOf(item),
            DeliveryState = DeliveryStateOf(OctorateJson.Text(item, "status"), direction),
        };
    }

    /// <summary>One page of messages as read from an answer.</summary>
    /// <param name="Messages">The messages that could be read.</param>
    /// <param name="ItemCount">How many items the answer had, the unreadable ones included.</param>
    /// <param name="TotalPages">The pages the wrapper says there are; <c>null</c> for a bare list.</param>
    internal sealed record ChatMessagePage(IReadOnlyList<OctorateChatMessage> Messages, int ItemCount, int? TotalPages);

    /// <summary>The direction of a message (see the remarks of the class).</summary>
    internal static OtaMessageDirection DirectionOf(JsonElement message, JsonElement sender, JsonElement externalSender)
    {
        switch (AttributeOf(message, FlowDirectionAttribute)?.ToUpperInvariant())
        {
            case "TO":
                return OtaMessageDirection.Outbound;
            case "FROM":
                return OtaMessageDirection.Inbound;
        }

        if (OctorateJson.Text(sender, "userId") is not null)
            return OtaMessageDirection.Outbound;

        if (OctorateJson.Text(sender, "externalGuest") is not null
            || OctorateJson.Text(sender, "guestId") is not null
            || externalSender.ValueKind == JsonValueKind.Object)
        {
            return OtaMessageDirection.Inbound;
        }

        return OtaMessageDirection.Outbound;
    }

    /// <summary>The delivery state Octorate reports; for an inbound message always sent, for an outbound one a word it does not know needs a person to check.</summary>
    internal static OtaMessageDeliveryState DeliveryStateOf(string? status, OtaMessageDirection direction)
    {
        if (direction == OtaMessageDirection.Inbound)
            return OtaMessageDeliveryState.Sent;

        return status?.Trim().ToUpperInvariant() switch
        {
            "CREATED" => OtaMessageDeliveryState.Created,
            "WAIT_AUTH" => OtaMessageDeliveryState.WaitingAuthorization,
            "SENT" => OtaMessageDeliveryState.Sent,
            "ERROR" => OtaMessageDeliveryState.Error,
            "MANUAL_VERIFY" => OtaMessageDeliveryState.ManualVerify,
            _ => OtaMessageDeliveryState.ManualVerify,
        };
    }

    /// <summary>The value of the first attribute of <paramref name="element"/> of the given type, <c>null</c> when it has none or it is blank.</summary>
    private static string? AttributeOf(JsonElement element, string type)
    {
        if (OctorateJson.Find(element, "attributes") is not { ValueKind: JsonValueKind.Array } attributes)
            return null;

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.ValueKind == JsonValueKind.Object
                && string.Equals(OctorateJson.Text(attribute, "type"), type, StringComparison.OrdinalIgnoreCase))
            {
                return OctorateJson.Text(attribute, "value");
            }
        }

        return null;
    }

    /// <summary>The element has an attribute of the given type, whatever its value.</summary>
    private static bool HasAttribute(JsonElement element, string type) =>
        OctorateJson.Find(element, "attributes") is { ValueKind: JsonValueKind.Array } attributes
        && attributes.EnumerateArray().Any(attribute =>
            attribute.ValueKind == JsonValueKind.Object
            && string.Equals(OctorateJson.Text(attribute, "type"), type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name of a person as people read it (first name, last name), else the descriptive name of the answer, else the user name.</summary>
    private static string? NameOf(JsonElement person)
    {
        var first = OctorateJson.Text(person, "firstname", "firstName");
        var last = OctorateJson.Text(person, "lastname", "lastName");
        var composed = string.Join(' ', new[] { first, last }.Where(part => part is not null));
        return composed.Length > 0
            ? composed
            : OctorateJson.Text(person, "fullname", "fullName") ?? OctorateJson.Text(person, "username");
    }

    /// <summary>The name of the first member of the thread that is a guest and not a user.</summary>
    private static string? GuestNameOf(JsonElement thread)
    {
        if (OctorateJson.Find(thread, "persons") is not { ValueKind: JsonValueKind.Array } persons)
            return null;

        foreach (var person in persons.EnumerateArray())
        {
            if (person.ValueKind != JsonValueKind.Object || OctorateJson.Text(person, "userId") is not null)
                continue;

            if (OctorateJson.Text(person, "guestId") is not null || OctorateJson.Text(person, "externalGuest") is not null)
                return NameOf(person);
        }

        return null;
    }

    private static IReadOnlyList<string> ConnectionIdsOf(JsonElement thread)
    {
        if (OctorateJson.Find(thread, "chatThreadExternals") is not { ValueKind: JsonValueKind.Array } externals)
            return [];

        return
        [
            .. externals.EnumerateArray()
                .Select(external => OctorateJson.Text(external, "connectionId"))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static DateTime? LastActivityOf(JsonElement thread) =>
        OctorateJson.Find(thread, "lastMessage") is { ValueKind: JsonValueKind.Object } last
            ? OctorateJson.Instant(last, "sentTime") ?? OctorateJson.Instant(last, "createTime")
            : null;

    private static DateTime? ReadAtOf(JsonElement message)
    {
        if (OctorateJson.Instant(message, "readTime") is { } direct)
            return direct;

        if (OctorateJson.Find(message, "readers") is not { ValueKind: JsonValueKind.Array } readers)
            return null;

        DateTime? first = null;
        foreach (var reader in readers.EnumerateArray())
        {
            if (OctorateJson.Instant(reader, "readTime") is { } at && (first is null || at < first))
                first = at;
        }

        return first;
    }

    private static (string? Url, string? ContentType) AttachmentOf(JsonElement message)
    {
        if (OctorateJson.Find(message, "attachment") is not { ValueKind: JsonValueKind.Object } attachment)
            return (null, null);

        var url = OctorateJson.Text(attachment, "url");
        if (url is null
            || url.Length > OtaMessage.AttachmentUrlMaxLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return (null, null);
        }

        var contentType = OctorateJson.Text(attachment, "type")?.ToUpperInvariant() switch
        {
            "JPEG" => "image/jpeg",
            "PNG" => "image/png",
            _ => "application/octet-stream",
        };
        return (url, contentType);
    }
}
