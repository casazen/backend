using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Infrastructure.Services;

internal sealed record PresetAgent(string Key, RoutineEventType Trigger, RoutineActionType Action, string English, string Italian)
{
    public Routine ExampleRoutine() => new() { EventType = Trigger, AgentObjective = Italian };
}

internal static class PresetAgentCatalog
{
    public static readonly PresetAgent[] All =
    [
        new("cleaning", RoutineEventType.AfterCheckout, RoutineActionType.CreateCleaningRequest,
            "Arrange cleaning after check-out for stays that took place.",
            "Organizza la pulizia dopo il check-out per i soggiorni che hanno avuto luogo."),
        new("arrival-instructions", RoutineEventType.BeforeCheckin, RoutineActionType.MessageToGuest,
            "Send arrival instructions for confirmed bookings.",
            "Invia le istruzioni di arrivo alle prenotazioni confermate."),
        new("review", RoutineEventType.AfterCheckout, RoutineActionType.RequestReview,
            "Invite guests to review completed direct stays.",
            "Invita alla recensione gli ospiti di soggiorni diretti conclusi."),
        new("document-reminder", RoutineEventType.BeforeCheckin, RoutineActionType.MessageToGuest,
            "Remind guests of document requirements before arrival for confirmed bookings.",
            "Ricorda gli adempimenti documentali prima dell’arrivo per le prenotazioni confermate."),
    ];

    public static PresetAgent? Match(Routine routine, IReadOnlyList<RoutineAction> actions) =>
        actions.Count == 1 ? All.FirstOrDefault(preset => preset.Trigger == routine.EventType &&
            preset.Action == actions[0].Type && (routine.AgentObjective == preset.Italian || routine.AgentObjective == preset.English)) : null;
}
