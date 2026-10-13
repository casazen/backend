using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What a stored supplier profile has to satisfy to be activated (SU-05, A4-09) and the five wizard steps derived from
/// it. Single source for the wizard status, the activation check and the guard that keeps an active profile complete.
/// </summary>
internal static class SupplierActivationRules
{
    /// <summary>The requirements of the profile (everything but the Terms of Service) that are not met, in wizard order.</summary>
    public static IReadOnlyList<(string Step, string Code)> ProfileBlockers(SupplierProfile profile)
    {
        var blockers = new List<(string, string)>();

        if (string.IsNullOrWhiteSpace(profile.LegalName))
            blockers.Add((SupplierActivation.Steps.Identity, SupplierActivation.Blockers.LegalNameMissing));
        if (!SupplierActivation.IsPlausiblePhone(profile.Phone))
            blockers.Add((SupplierActivation.Steps.Identity, SupplierActivation.Blockers.PhoneInvalid));

        // Only known codes count: a stored code that is not a category (old data) matches no host request.
        if (!SupplierComuneMatcher.ReadStrings(profile.CategoriesJson).Any(ServiceCategories.IsKnown))
            blockers.Add((SupplierActivation.Steps.Services, SupplierActivation.Blockers.CategoriesMissing));
        if (!HasComuni(profile))
            blockers.Add((SupplierActivation.Steps.Services, SupplierActivation.Blockers.ComuniMissing));

        if (string.IsNullOrWhiteSpace(profile.Bio))
            blockers.Add((SupplierActivation.Steps.Profile, SupplierActivation.Blockers.BioMissing));

        return blockers;
    }

    public static bool HasComuni(SupplierProfile profile) =>
        SupplierComuneMatcher.ReadStrings(profile.ComuniJson).Any(c => !string.IsNullOrWhiteSpace(c))
        || SupplierComuneMatcher.ReadStrings(profile.ComuneIstatCodesJson).Any(c => !string.IsNullOrWhiteSpace(c));

    public static SupplierTosState TosState(SupplierProfile profile, string currentVersion)
    {
        var accepted = profile.TosAcceptedAt is not null;
        var isCurrent = accepted && string.Equals(profile.TosVersion, currentVersion, StringComparison.Ordinal);
        return new SupplierTosState(
            currentVersion,
            profile.TosVersion,
            profile.TosAcceptedAt,
            ReacceptanceRequired: accepted && !isCurrent,
            // A recorded version that is no longer the current one blocks supplier actions; an unrecorded (legacy)
            // acceptance only asks to accept again.
            BlocksActions: accepted && profile.TosVersion is not null && !isCurrent);
    }

    public static List<ActivationStep> Steps(SupplierProfile profile, SupplierTosState tos)
    {
        var blockers = ProfileBlockers(profile);
        ActivationStep Required(string step)
        {
            var first = blockers.FirstOrDefault(b => b.Step == step);
            return first.Code is null
                ? new ActivationStep(step, SupplierActivation.StepStatus.Completed)
                : new ActivationStep(step, SupplierActivation.StepStatus.Pending, first.Code);
        }

        var hasPhotos = HasPhotos(profile);
        var tosDone = profile.TosAcceptedAt is not null && !tos.ReacceptanceRequired;

        return
        [
            Required(SupplierActivation.Steps.Identity),
            Required(SupplierActivation.Steps.Services),
            new ActivationStep(
                SupplierActivation.Steps.Showcase,
                hasPhotos ? SupplierActivation.StepStatus.Completed : SupplierActivation.StepStatus.Pending,
                Required: false),
            Required(SupplierActivation.Steps.Profile),
            new ActivationStep(
                SupplierActivation.Steps.Terms,
                tosDone ? SupplierActivation.StepStatus.Completed : SupplierActivation.StepStatus.Pending,
                tosDone ? null : (tos.ReacceptanceRequired
                    ? SupplierActivation.Blockers.TosReacceptanceRequired
                    : SupplierActivation.Blockers.TosNotAccepted)),
        ];
    }

    private static bool HasPhotos(SupplierProfile profile)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(profile.PhotoUrlsJson) ? "[]" : profile.PhotoUrlsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
