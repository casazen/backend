using Casazen.Core.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Crm</c> (E07/F06/US01/T01, NF-95): the settings the marketing module of epic E07 shares: the key of the keyed index of the
/// contacts and of the suppression list, the two switches that wait for a decision of the product owner, the relay domains of the OTAs
/// and the limits of the double opt-in. Every value but the key has a default; the key is a secret and has none. Validated at startup
/// (<see cref="CrmOptionsValidator"/>). Runbook <c>docs/runbooks/crm-marketing.md</c>.
/// </summary>
public class CrmOptions
{
    public const string SectionName = "Crm";

    /// <summary>Shortest accepted key, in characters: 32 random bytes written as text are 44.</summary>
    public const int MinContactIndexKeyLength = 32;

    /// <summary>Shortest and longest time a confirmation link works, in hours (an hour to thirty days).</summary>
    public const int MinConfirmationLinkValidityHours = 1;

    /// <inheritdoc cref="MinConfirmationLinkValidityHours"/>
    public const int MaxConfirmationLinkValidityHours = 24 * 30;

    /// <summary>Fewest and most confirmation messages sent to one contact.</summary>
    public const int MinConfirmationMaxSends = 1;

    /// <inheritdoc cref="MinConfirmationMaxSends"/>
    public const int MaxConfirmationMaxSends = 10;

    /// <summary>
    /// The secret of the keyed index (<c>Crm:ContactIndexKey</c>, env var <c>Crm__ContactIndexKey</c>): at least
    /// <see cref="MinContactIndexKeyLength"/> random characters, never in the database and never in the repository. <b>It must never change
    /// once contacts exist</b>: every hash already stored would stop matching and the suppression list would stop honoring the opt-outs
    /// it holds. Outside Development and Testing, with <c>Features:CrmMarketing</c> on, the application does not start without it; in
    /// Development and Testing a fixed public key stands in (<c>ICrmContactIndex</c>).
    /// </summary>
    public string? ContactIndexKey { get; set; }

    /// <summary>
    /// Whether the contacts of the guests are read from Octorate (<c>contactInfo</c>, one booking at a time) and recorded for the service
    /// (messages, check-in, invoice). Off by default: the product owner turns it on once the static IP addresses are whitelisted by Octorate.
    /// Env var <c>Crm__OctorateContactsEnabled</c>.
    /// </summary>
    public bool OctorateContactsEnabled { get; set; }

    /// <summary>
    /// Whether a customer of the booking engine of the agency's own site becomes a contact on soft opt-in for a confirmed booking. Off by
    /// default; turning it on is the product owner's declaration that the terms of Octorate allow it (D24) and that the privacy notice is
    /// shown at the booking (D34). Env var <c>Crm__BookingEngineSoftOptIn</c>.
    /// </summary>
    public bool BookingEngineSoftOptIn { get; set; }

    /// <summary>
    /// Bare host names (<c>guest.example.com</c>: no scheme, no path, no port, no address) of the relay addresses the OTAs give to their guests.
    /// An address on one of them is an OTA forward: service only, never marketing. The list is empty by default: only domains confirmed against
    /// real bookings belong in it (the sandbox of Octorate answers the flags <c>emailAvailable</c> and <c>phoneAvailable</c>, not the addresses).
    /// Env vars <c>Crm__OtaForwardDomains__0</c>, <c>Crm__OtaForwardDomains__1</c>…
    /// </summary>
    public List<string> OtaForwardDomains { get; set; } = [];

    /// <summary>Hours a confirmation link (double opt-in) works (72). Env var <c>Crm__ConfirmationLinkValidityHours</c>.</summary>
    public int ConfirmationLinkValidityHours { get; set; } = 72;

    /// <summary>How many confirmation messages one contact gets at most (3). Env var <c>Crm__ConfirmationMaxSends</c>.</summary>
    public int ConfirmationMaxSends { get; set; } = 3;

    /// <summary>
    /// True when <paramref name="value"/> is a bare host name: dot-separated labels of letters, digits and hyphens (1 to 63 characters, none
    /// starting or ending with a hyphen), at least two of them, not an IP address, with nothing around it. A scheme, a path, a port, a
    /// wildcard, an address with <c>@</c> and a trailing dot are not.
    /// </summary>
    public static bool IsBareHostName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253)
            return false;

        var labels = value.Split('.');
        if (labels.Length < 2)
            return false;

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
                return false;

            foreach (var character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                    return false;
            }
        }

        // 192.0.2.1 has valid labels and is not a host name: the last label of a name is never only digits.
        return !labels[^1].All(char.IsAsciiDigit);
    }
}

/// <summary>
/// Refuses at startup a CRM configuration that is not valid, with the list of problems (the pattern of
/// <see cref="ShowcaseBookingOptionsValidator"/>). The limits of the double opt-in are checked whatever the flag says, so a typo never
/// changes a deadline silently. Outside Development and Testing: with <c>Features:CrmMarketing</c> on the key of the index is required;
/// a key that is set must be long enough and every relay domain must be a bare host name, whatever the flag says, because the contacts
/// and the consents of the check-in are recorded (and the suppression list is written) with the flag off too.
/// </summary>
public sealed class CrmOptionsValidator(IFeatureFlags featureFlags, IHostEnvironment environment) : IValidateOptions<CrmOptions>
{
    public ValidateOptionsResult Validate(string? name, CrmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.ConfirmationLinkValidityHours is < CrmOptions.MinConfirmationLinkValidityHours or > CrmOptions.MaxConfirmationLinkValidityHours)
        {
            failures.Add(
                $"Crm__ConfirmationLinkValidityHours must be between {CrmOptions.MinConfirmationLinkValidityHours} and {CrmOptions.MaxConfirmationLinkValidityHours} hours.");
        }

        if (options.ConfirmationMaxSends is < CrmOptions.MinConfirmationMaxSends or > CrmOptions.MaxConfirmationMaxSends)
        {
            failures.Add(
                $"Crm__ConfirmationMaxSends must be between {CrmOptions.MinConfirmationMaxSends} and {CrmOptions.MaxConfirmationMaxSends}.");
        }

        if (RequiresKey(environment))
        {
            var key = options.ContactIndexKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                if (featureFlags.IsEnabled(FeatureFlags.CrmMarketing))
                {
                    failures.Add(
                        "Crm__ContactIndexKey is missing: Features__CrmMarketing is on, and the contacts of the CRM and the suppression list are "
                        + "indexed by an HMAC keyed by it (docs/runbooks/crm-marketing.md).");
                }
            }
            else if (key.Length < CrmOptions.MinContactIndexKeyLength)
            {
                // A key that is set is the key of every hash from then on: it cannot be corrected later, so a short one is refused now.
                failures.Add(
                    $"Crm__ContactIndexKey is too short: use at least {CrmOptions.MinContactIndexKeyLength} random characters.");
            }

            for (var index = 0; index < options.OtaForwardDomains.Count; index++)
            {
                if (!CrmOptions.IsBareHostName(options.OtaForwardDomains[index]))
                {
                    failures.Add(
                        $"Crm__OtaForwardDomains__{index} is not a bare host name: write guest.example.com, without a scheme, a path, a port or an address.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>True where the key cannot be left out: every environment except Development and Testing.</summary>
    public static bool RequiresKey(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}
