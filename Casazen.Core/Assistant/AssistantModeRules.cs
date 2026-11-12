using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Assistant;

/// <summary>
/// The rule of the modes of the assistant (E05/F05/US01/T03, NF-67, AC-NF67-3), pure: <b>a situation that carries a sensitive category (money, cancellation and refund, complaint,
/// damage, safety, legal matters, personal data), and <see cref="AssistantSituation.Other"/>, can be set to <see cref="AssistantMode.Escalate"/> or
/// <see cref="AssistantMode.Draft"/> and never to <see cref="AssistantMode.Automatic"/> or <see cref="AssistantMode.AutomaticWithExceptions"/></b>, whoever asks. The service and
/// the endpoints that save a mode call <see cref="Validate"/>; the auto-send gate of the later tasks repeats it on what it reads (<see cref="Effective"/>).
/// </summary>
public static class AssistantModeRules
{
    /// <summary>The 422 of a mode the situation does not allow (an automatic one for a sensitive situation, or a value that is no mode).</summary>
    public const string ModeNotAllowedCode = "assistant_mode_not_allowed";

    /// <summary>True when <paramref name="mode"/> is one of the modes <paramref name="situation"/> allows.</summary>
    public static bool IsAllowed(AssistantSituation situation, AssistantMode mode) =>
        AssistantSituationCatalog.Of(situation).AllowedModes.Contains(mode);

    /// <summary>422 <see cref="ModeNotAllowedCode"/> unless <paramref name="mode"/> is one of the modes <paramref name="situation"/> allows.</summary>
    public static void Validate(AssistantSituation situation, AssistantMode mode)
    {
        if (!IsAllowed(situation, mode))
            throw new DomainRuleException(ModeNotAllowedCode, "AssistantModeNotAllowed");
    }

    /// <summary>
    /// The mode to act on for a mode that was stored: the stored one when the situation allows it, <see cref="AssistantMode.Draft"/> when it does not (a row written by a version
    /// that did not know the rule, or by hand): the fail-closed side. An automatic mode never survives on a sensitive situation.
    /// </summary>
    public static AssistantMode Effective(AssistantSituation situation, AssistantMode stored) =>
        IsAllowed(situation, stored) ? stored : AssistantMode.Draft;

    /// <summary>True for the two modes in which the assistant may send by itself.</summary>
    public static bool IsAutomatic(AssistantMode mode) => mode is AssistantMode.Automatic or AssistantMode.AutomaticWithExceptions;
}
