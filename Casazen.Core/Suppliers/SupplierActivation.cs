namespace Casazen.Core.Suppliers;

/// <summary>
/// The activation wizard of a supplier (SU-05, A4-09 / A4-31): its five steps and the stable codes of what keeps a profile
/// from being activated. Only a profile that satisfies every <see cref="Blockers"/> requirement is shown to hosts, so the
/// requirements are checked on the stored profile, never on what the client says it did.
/// </summary>
public static class SupplierActivation
{
    /// <summary>Ids of the five wizard steps, in order (the position is the step number 1-5).</summary>
    public static class Steps
    {
        /// <summary>1. Business name and phone.</summary>
        public const string Identity = "identity";

        /// <summary>2. Service categories and comuni.</summary>
        public const string Services = "services";

        /// <summary>3. Showcase photos (optional).</summary>
        public const string Showcase = "showcase";

        /// <summary>4. Professional description.</summary>
        public const string Profile = "profile";

        /// <summary>5. Availability (optional) and acceptance of the Terms of Service.</summary>
        public const string Terms = "terms";

        public static IReadOnlyList<string> All { get; } = [Identity, Services, Showcase, Profile, Terms];
    }

    /// <summary>Codes of the requirements still missing (<c>blockers</c> of the 409 and of the wizard steps).</summary>
    public static class Blockers
    {
        public const string LegalNameMissing = "legal_name_missing";
        public const string PhoneInvalid = "phone_invalid";
        public const string CategoriesMissing = "categories_missing";
        public const string ComuniMissing = "comuni_missing";
        public const string BioMissing = "bio_missing";
        public const string TosNotAccepted = "tos_not_accepted";

        /// <summary>Step 5 of an active supplier whose accepted Terms version is not the current one.</summary>
        public const string TosReacceptanceRequired = "tos_reacceptance_required";
    }

    /// <summary>Statuses of a wizard step.</summary>
    public static class StepStatus
    {
        public const string Completed = "completed";
        public const string Pending = "pending";
    }

    public const string BlockedCode = "supplier_activation_blocked";
    public const string BlockedMessageKey = "SupplierActivationBlocked";

    public const string ProfileRequirementsCode = "supplier_profile_requirements";
    public const string ProfileRequirementsMessageKey = "SupplierProfileRequirements";

    public const string TosVersionStaleCode = "supplier_tos_version_stale";
    public const string TosVersionStaleMessageKey = "SupplierTosVersionStale";

    public const string TosReacceptanceRequiredCode = "supplier_tos_reacceptance_required";
    public const string TosReacceptanceRequiredMessageKey = "SupplierTosReacceptanceRequired";

    public const string StepInvalidCode = "supplier_activation_step_invalid";
    public const string StepInvalidMessageKey = "SupplierActivationStepInvalid";

    public const int StepCount = 5;

    /// <summary>
    /// Whether <paramref name="phone"/> can be a phone number: digits with an optional leading <c>+</c> and the usual
    /// separators (space, <c>-</c>, <c>.</c>, parentheses), 6 to 15 digits (E.164 allows at most 15). It does not prove the
    /// number is reachable: no SMS verification exists yet (see the runbook).
    /// </summary>
    public static bool IsPlausiblePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return false;

        var digits = 0;
        var trimmed = phone.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is >= '0' and <= '9')
                digits++;
            else if (c == '+' && i == 0)
                continue;
            else if (c is ' ' or '-' or '.' or '(' or ')')
                continue;
            else
                return false;
        }

        return digits is >= 6 and <= 15;
    }
}

/// <summary>
/// The activation of the profile cannot be completed: <see cref="Blockers"/> lists the requirements still missing
/// (<see cref="SupplierActivation.Blockers"/>). HTTP 409.
/// </summary>
public class SupplierActivationBlockedException(IReadOnlyList<string> blockers)
    : Exceptions.DomainConflictException(SupplierActivation.BlockedCode, SupplierActivation.BlockedMessageKey)
{
    public IReadOnlyList<string> Blockers { get; } = blockers;
}
