using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The AI assistant that answers the guests' messages (epic E05, feature F05, ADR-032, NF-66, NF-67): the one place where its services are registered. An extension class of its
/// own, so this feature never collides with the shared <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once and the other tasks of the feature (the drafts, the lessons,
/// the auto-send gate) add their registrations here. Nothing here is conditional: what is not ready sits behind <c>Features:MessageAssistant</c> where it is used (the controllers, and
/// the handlers of the later tasks), not by being left out of DI.
/// </summary>
public static class MessageAssistantServiceCollectionExtensions
{
    public static IServiceCollection AddMessageAssistant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F05/US01/T02 (NF-66): the rules of the organization with the exceptions of a property (and the version of the instructions every change grows), and the knowledge base
        // of a property: the facts of its listing, the entries of the property and of the organization. Scoped: they hold the database of the request.
        services.AddScoped<IAssistantRulesService, AssistantRulesService>();
        services.AddScoped<IAssistantKnowledgeService, AssistantKnowledgeService>();

        // E05/F05/US01/T03 (NF-66, NF-67): what the assistant does in each situation (mode, template, source, the start of the trust period) for the organization and as the
        // exceptions of a property; it grows the version of the instructions through IAssistantRulesService.
        services.AddScoped<IAssistantSituationService, AssistantSituationService>();

        // E05/F05/US02/T01 (NF-67): the pipeline of the drafts (section Assistant), the gate that never sends by itself until E05/F05/US03 registers the real one, the observer that
        // closes the «To do» of an escalated message when a person answers its conversation, the handler of the messages of the guests and its job (both behind the flag).
        services.AddOptions<AssistantOptions>().BindConfiguration(AssistantOptions.SectionName);
        services.AddScoped<IAssistantDraftService, AssistantDraftService>();
        services.AddScoped<IAutoSendGate, NeverAutoSendGate>();
        services.AddScoped<IThreadReplyObserver, AssistantEscalationCloser>();
        services.AddScoped<IEventHandler<DomainEvent>, AssistantMessageHandler>();
        services.AddScoped<AssistantDraftJob>();

        return services;
    }
}
