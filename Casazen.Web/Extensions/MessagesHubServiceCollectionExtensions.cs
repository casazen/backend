using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The hub of messages (epic E05, feature F02, ADR-032, NF-61, NF-64): the one list and the one thread model over the conversations of the channels
/// and the direct ones, and the proxy of the attachments. An extension class of its own, so this feature never collides with the shared
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once and the next tasks of the feature (reply, templates, notifications,
/// assistant) add their registrations here. Nothing here is conditional: the hub reads what the copy of the conversations (<c>AddMessageSync</c>)
/// holds, and answers an empty OTA part while the connector is off.
/// </summary>
public static class MessagesHubServiceCollectionExtensions
{
    public static IServiceCollection AddMessagesHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IMessageHubService, MessageHubService>();
        services.AddScoped<StorefrontChatService>();

        services.AddScoped<IMessageReplyService, MessageReplyService>();

        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<IMessageTranslationService, MessageTranslationService>();

        services.AddHttpClient<IOtaAttachmentFetcher, OtaAttachmentFetcher>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(OtaAttachmentFetcher.CreateHandler)
            .RemoveAllLoggers();

        return services;
    }
}
