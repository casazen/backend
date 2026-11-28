using Casazen.Core.Features;
using Casazen.Core.Suppliers;
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

    public const int MinEmailVerificationMinutes = 5;

    public const int MaxEmailVerificationMinutes = 24 * 60;

    public const int MinProposalResponseMinutes = 30;

    public const int MaxProposalResponseMinutes = 7 * 24 * 60;

    public const int MinFreeCancellationHours = 0;

    public const int MaxFreeCancellationHours = 24 * 30;

    public int EmailVerificationMinutes { get; set; } = 30;

    public int ProposalResponseMinutes { get; set; } = 24 * 60;

    public int InquiryResponseMinutes { get; set; } = ShowcaseInquiryLimits.DefaultResponseMinutes;

    public int FreeCancellationHours { get; set; } = 24;

    public string? PrivacyNoticeVersion { get; set; }

    public string? CurrentPrivacyNoticeVersion =>
        string.IsNullOrWhiteSpace(PrivacyNoticeVersion) ? null : PrivacyNoticeVersion.Trim();

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

        if (InquiryResponseMinutes is < ShowcaseInquiryLimits.MinResponseMinutes or > ShowcaseInquiryLimits.MaxResponseMinutes)
        {
            failures.Add(
                $"Suppliers__Showcase__InquiryResponseMinutes must be between {ShowcaseInquiryLimits.MinResponseMinutes} and {ShowcaseInquiryLimits.MaxResponseMinutes} minutes.");
        }

        if (FreeCancellationHours is < MinFreeCancellationHours or > MaxFreeCancellationHours)
        {
            failures.Add(
                $"Suppliers__Showcase__FreeCancellationHours must be between {MinFreeCancellationHours} and {MaxFreeCancellationHours} hours.");
        }

        if (CurrentPrivacyNoticeVersion is { Length: > 50 })
            failures.Add("Suppliers__Showcase__PrivacyNoticeVersion is longer than 50 characters.");

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
    public const string SectionName = "Suppliers";

    public const int MinKeyLength = 32;

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

    public static bool RequiresKey(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}
