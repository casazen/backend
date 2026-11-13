using Casazen.Core.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>OwnerPortal</c> (E07/F07/US01/T02, NF-111): the settings of the owner portal operations. Today one, the secret of the keyed index of the delegated
/// contacts of an owner (<see cref="ContactIndexKey"/>), which finds and keeps unique an address while the address is encrypted at rest. Validated at startup
/// (<see cref="OwnerPortalOptionsValidator"/>). Runbook <c>docs/runbooks/deploy-checklist.md</c>, <c>docs/runbooks/encryption.md</c>.
/// </summary>
public class OwnerPortalOptions
{
    public const string SectionName = "OwnerPortal";

    /// <summary>Shortest accepted key, in characters: 32 random bytes written as text are 44.</summary>
    public const int MinContactIndexKeyLength = 32;

    /// <summary>
    /// The secret of the keyed index of the contacts (<c>OwnerPortal:ContactIndexKey</c>, env var <c>OwnerPortal__ContactIndexKey</c>): at least
    /// <see cref="MinContactIndexKeyLength"/> random characters, never in the database and never in the repository. <b>It must never change once contacts
    /// exist</b>: every hash already stored would stop matching (the same address could be invited twice). Outside Development and Testing, with
    /// <c>Features:AgencyTier</c> on, the application does not start without it; with the flag off it starts (the endpoints answer 404). In Development and
    /// Testing a fixed public key stands in (<c>IOwnerContactIndex</c>).
    /// </summary>
    public string? ContactIndexKey { get; set; }
}

/// <summary>
/// Refuses at startup an owner portal configuration that is not valid (the pattern of <see cref="CrmOptionsValidator"/>): outside Development and Testing the
/// key of the index of the contacts is required with <c>Features:AgencyTier</c> on, and a key that is set must be long enough whatever the flag says, because it
/// is the key of every hash from then on.
/// </summary>
public sealed class OwnerPortalOptionsValidator(IFeatureFlags featureFlags, IHostEnvironment environment) : IValidateOptions<OwnerPortalOptions>
{
    public ValidateOptionsResult Validate(string? name, OwnerPortalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (RequiresKey(environment))
        {
            var key = options.ContactIndexKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                if (featureFlags.IsEnabled(FeatureFlags.AgencyTier))
                {
                    failures.Add(
                        "OwnerPortal__ContactIndexKey is missing: Features__AgencyTier is on, and the delegated contacts of the owners are found by an HMAC keyed by it "
                        + "(docs/runbooks/deploy-checklist.md).");
                }
            }
            else if (key.Length < OwnerPortalOptions.MinContactIndexKeyLength)
            {
                // A key that is set is the key of every hash from then on: it cannot be corrected later, so a short one is refused now.
                failures.Add(
                    $"OwnerPortal__ContactIndexKey is too short: use at least {OwnerPortalOptions.MinContactIndexKeyLength} random characters.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>True where the key cannot be left out: every environment except Development and Testing.</summary>
    public static bool RequiresKey(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}
