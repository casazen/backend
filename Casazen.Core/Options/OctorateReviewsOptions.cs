namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:Reviews</c> section (E05/F06/US01/T02, OCT-12): how many bookings the sync of the reviews of the channels reads in one run. The Octorate API has no
/// list per property, so a booking is one call: the cap keeps a run from using the calls of the orgs on reviews. Env var <c>Octorate__Reviews__MaxBookingsPerRun</c>; nothing
/// is required, and the committed value is 200.
/// </summary>
public sealed class OctorateReviewsOptions
{
    public const string SectionName = "Octorate:Reviews";

    /// <summary>The most bookings one run of <c>ota-review-sync</c> reads, over all the orgs (200 by default, 1 to 2000). The ones left wait for the next run, six hours later.</summary>
    public int MaxBookingsPerRun { get; set; } = 200;

    /// <summary>The upper limit of <see cref="MaxBookingsPerRun"/>.</summary>
    public const int MaxBookingsPerRunLimit = 2000;

    /// <summary>The value to use: <see cref="MaxBookingsPerRun"/> when it is in range, else the default (a wrong value never stops the job or lets it read without a limit).</summary>
    public int EffectiveMaxBookingsPerRun => MaxBookingsPerRun is >= 1 and <= MaxBookingsPerRunLimit ? MaxBookingsPerRun : 200;
}
