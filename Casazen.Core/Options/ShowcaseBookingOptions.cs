using Casazen.Core.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Suppliers:Showcase</c> (SP-10): the booking from a supplier's public showcase. Every value has a default except the
/// version of the privacy notice, which is a legal decision of the product owner and is never invented (as the
/// <c>Gdpr</c> versions, D14): with the flag <c>SupplierShowcaseBooking</c> on it has to be set, or the application does not
/// start. Validated at startup (<see cref="ShowcaseBookingOptionsValidator"/>). Runbook <c>docs/runbooks/suppliers.md</c>, section 23.
/// </summary>
public class ShowcaseBookingOptions
{
    public const string SectionName = "Suppliers:Showcase";

    /// <summary>Shortest and longest time a customer has to check the e-mail, in minutes (5 minutes to a day).</summary>
    public const int MinEmailVerificationMinutes = 5;

    /// <inheritdoc cref="MinEmailVerificationMinutes"/>
    public const int MaxEmailVerificationMinutes = 24 * 60;

    /// <summary>Shortest and longest time a customer has to answer another time the supplier proposed, in minutes (30 minutes to a week).</summary>
    public const int MinProposalResponseMinutes = 30;

    /// <inheritdoc cref="MinProposalResponseMinutes"/>
    public const int MaxProposalResponseMinutes = 7 * 24 * 60;

    /// <summary>
    /// Minutes a booking waits for the customer to check the e-mail (30): the slot is held for that long and the link works
    /// for that long. Env var <c>Suppliers__Showcase__EmailVerificationMinutes</c>.
    /// </summary>
    public int EmailVerificationMinutes { get; set; } = 30;

    /// <summary>
    /// Minutes the customer has to answer another time the supplier proposed for a booking (a day): the request waits that long
    /// for the answer, then it lapses like a request nobody answered (the answer itself arrives with SP-11). Env var
    /// <c>Suppliers__Showcase__ProposalResponseMinutes</c>.
    /// </summary>
    public int ProposalResponseMinutes { get; set; } = 24 * 60;

    /// <summary>
    /// The version of the privacy notice a customer accepts when it books (the text the web app shows): the booking is refused
    /// (422 <c>supplier_booking_consent_outdated</c>) when the customer accepted another one. <b>No default.</b> Env var
    /// <c>Suppliers__Showcase__PrivacyNoticeVersion</c>.
    /// </summary>
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>The configured version, trimmed, or <c>null</c> when it is missing.</summary>
    public string? CurrentPrivacyNoticeVersion =>
        string.IsNullOrWhiteSpace(PrivacyNoticeVersion) ? null : PrivacyNoticeVersion.Trim();

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate(bool bookingEnabled)
    {
        var failures = new List<string>();
        if (EmailVerificationMinutes is < MinEmailVerificationMinutes or > MaxEmailVerificationMinutes)
        {
            failures.Add(
                $"Suppliers__Showcase__EmailVerificationMinutes must be between {MinEmailVerificationMinutes} and {MaxEmailVerificationMinutes} minutes.");
        }

        if (ProposalResponseMinutes is < MinProposalResponseMinutes or > MaxProposalResponseMinutes)
        {
            failures.Add(
                $"Suppliers__Showcase__ProposalResponseMinutes must be between {MinProposalResponseMinutes} and {MaxProposalResponseMinutes} minutes.");
        }

        if (CurrentPrivacyNoticeVersion is { Length: > 50 })
            failures.Add("Suppliers__Showcase__PrivacyNoticeVersion is longer than 50 characters.");

        // The flag decides: while it is off nobody can book, so the version is not needed; with it on, a booking without the
        // version of the notice the customer accepted cannot be recorded, and the application must not run like that.
        if (bookingEnabled && CurrentPrivacyNoticeVersion is null)
        {
            failures.Add(
                "Suppliers__Showcase__PrivacyNoticeVersion is missing: Features__SupplierShowcaseBooking is on, and a booking records "
                + "the version of the privacy notice the customer accepted (docs/runbooks/suppliers.md, section 23).");
        }

        return failures;
    }
}

/// <summary>
/// The key of the e-mail index of the customers of the suppliers (<c>Suppliers:CustomerIndexKey</c>, SP-10): the secret of an
/// HMAC-SHA256 that finds a customer by its address without keeping the address searchable. It is not in the database and not
/// in the repository. Outside Development and Testing it is <b>required</b> where the booking can run (the flag is on); in
/// Development and Testing, when it is empty, a fixed key that protects nothing is used and the log says so.
/// </summary>
public class ServiceCustomerIndexOptions
{
    /// <summary>The section that holds the key: <c>Suppliers</c> (the key sits next to the sections of the suppliers).</summary>
    public const string SectionName = "Suppliers";

    /// <summary>Shortest accepted key, in characters: 32 random bytes written as text are 44.</summary>
    public const int MinKeyLength = 32;

    /// <summary>Env var <c>Suppliers__CustomerIndexKey</c>: at least <see cref="MinKeyLength"/> characters, random, never reused.</summary>
    public string? CustomerIndexKey { get; set; }
}

/// <summary>
/// Refuses at startup a booking configuration that is not valid, with the list of problems (the pattern of
/// <c>ServiceRequestOptionsValidator</c>): a typo never changes a deadline silently, and a booking that cannot record its
/// consent or index its customers does not run.
/// </summary>
public sealed class ShowcaseBookingOptionsValidator(IFeatureFlags featureFlags, IHostEnvironment environment)
    : IValidateOptions<ShowcaseBookingOptions>, IValidateOptions<ServiceCustomerIndexOptions>
{
    private bool BookingEnabled => featureFlags.IsEnabled(FeatureFlags.SupplierShowcaseBooking);

    public ValidateOptionsResult Validate(string? name, ShowcaseBookingOptions options)
    {
        var failures = options.Validate(BookingEnabled);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public ValidateOptionsResult Validate(string? name, ServiceCustomerIndexOptions options)
    {
        var key = options.CustomerIndexKey?.Trim();
        if (!string.IsNullOrEmpty(key) && key.Length < ServiceCustomerIndexOptions.MinKeyLength)
        {
            return ValidateOptionsResult.Fail(
                $"Suppliers__CustomerIndexKey is too short: use at least {ServiceCustomerIndexOptions.MinKeyLength} random characters.");
        }

        if (string.IsNullOrEmpty(key) && BookingEnabled && RequiresKey(environment))
        {
            return ValidateOptionsResult.Fail(
                "Suppliers__CustomerIndexKey is missing: Features__SupplierShowcaseBooking is on, and the customers of the suppliers are "
                + "indexed by an HMAC of their e-mail keyed by it (docs/runbooks/suppliers.md, section 23).");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>True where the key cannot be left out: every environment except Development and Testing.</summary>
    public static bool RequiresKey(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}
