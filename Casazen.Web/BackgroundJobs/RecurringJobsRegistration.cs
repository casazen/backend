using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring jobs, registered (<c>AddOrUpdate</c>) on every startup in the environment's own Hangfire schema.
/// Every job listed here must carry <see cref="DisableConcurrentExecutionAttribute"/> (FD-11): a run that is
/// still in progress, a retry or a manual trigger from the dashboard must never overlap with the next run.
/// A job behind a feature flag that is off is removed (<c>RemoveIfExists</c>), so an earlier deploy's schedule stops.
/// </summary>
public static class RecurringJobsRegistration
{
    public const string OtaSyncAllJobId = "ota-sync-all";
    public const string BookingPullAllJobId = "booking-pull-all";

    public static void Configure(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureOtaPartnerJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OtaPartnerApi));

        ConfigureServiceRequestAutoCancel(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.SupplierRequestAutoCancel));

        // SP-10: the upkeep of the bookings from the suppliers' public showcases is registered whatever the flags say. A hold that
        // was made while SupplierShowcaseBooking was on has to lapse, a request nobody answered has to be cancelled and a booking
        // that exists keeps its reminder, also after the flag is turned off; with the flag off these runs just find nothing.
        recurringJobManager.AddOrUpdate<ServiceRequestExpiryJob>(
            ServiceRequestExpiryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestExpiryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ServiceRequestReminderJob>(
            ServiceRequestReminderJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<DynamicPricingJob>(
            DynamicPricingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GdprDataRetentionJob>(
            GdprDataRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-10: one hourly job for the stay alerts; the two jobs it replaces are removed from the schedule.
        foreach (var replaced in StayAlertsJob.ReplacedRecurringJobIds)
            recurringJobManager.RemoveIfExists(replaced);

        recurringJobManager.AddOrUpdate<StayAlertsJob>(
            StayAlertsJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-20: daily CIN alert, after the nightly compliance check of CO-06.
        recurringJobManager.AddOrUpdate<CinDeadlineAlertJob>(
            CinDeadlineAlertJob.RecurringJobId,
            job => job.ExecuteAsync(),
            CinDeadlineAlertJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // AM-02: reminder of the third day, expiry and deletion of the closed org invitations. Always registered: the
        // retention of the personal data of an invitation does not depend on the OrgTeam flag; the service itself sends
        // no email while the flag is off.
        recurringJobManager.AddOrUpdate<OrgInvitationMaintenanceJob>(
            OrgInvitationMaintenanceJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgInvitationMaintenanceJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureESignProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ESignProvider));

        ConfigureRliProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.RliProvider));

        ConfigurePropertyModeJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.PropertyModeChange));

        recurringJobManager.AddOrUpdate<RliDeadlineReminderJob>(
            "rli-deadline-reminder",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<SeoContentRefreshJob>(
            "seo-content-refresh",
            job => job.ExecuteAsync(),
            "0 4 1 * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // SE-04: the events of the SEO funnel are deleted after Seo:Events:RetentionDays, nightly.
        recurringJobManager.AddOrUpdate<SeoEventRetentionJob>(
            SeoEventRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "30 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<DirectBookingChargeJob>(
            "direct-booking-charge",
            job => job.ExecuteAsync(),
            "0 6 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LT-06: payment requests of the rent installments coming due, payments in flight read again.
        recurringJobManager.AddOrUpdate<RentCollectionJob>(
            RentCollectionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            RentCollectionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CheckoutHoldExpiryJob>(
            CheckoutHoldExpiryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CheckoutHoldExpiryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // BK-17: custom domains are checked again (activation, removed DNS records, domains dropped by their host).
        recurringJobManager.AddOrUpdate<DomainRecheckJob>(
            DomainRecheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            DomainRecheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<IcalSupplierSyncJob>(
            "ical-supplier-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",  // Every 15 minutes
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<PropertyICalSyncJob>(
            "property-ical-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GuestCheckInSendJob>(
            "guest-checkin-send",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // MO-04: Expo receipts of the pushes (DeviceNotRegistered removes the device), purge of old delivery rows.
        recurringJobManager.AddOrUpdate<PushReceiptsJob>(
            PushReceiptsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PushReceiptsJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-06: nightly compliance check of every published property (suspends the ones that lost a requirement).
        recurringJobManager.AddOrUpdate<PropertyComplianceCheckJob>(
            PropertyComplianceCheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyComplianceCheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-18 (PO 2026-10-08): placeholder — runs weekly to check/update official fiscal rates when a source is configured.
        recurringJobManager.AddOrUpdate<FiscalRatesUpdateJob>(
            FiscalRatesUpdateJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 5 * * 1",  // every Monday at 05:00 UTC
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// Automatic cancellation of the service requests nobody answered (SP-04, D8): only with
    /// <see cref="FeatureFlags.SupplierRequestAutoCancel"/> on. With the flag off no request is ever cancelled by time, and the
    /// schedule of an earlier deploy is removed.
    /// </summary>
    private static void ConfigureServiceRequestAutoCancel(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(ServiceRequestAutoCancelJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<ServiceRequestAutoCancelJob>(
            ServiceRequestAutoCancelJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestAutoCancelJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// Application of the scheduled changes of rental mode (PM-02): only with <see cref="FeatureFlags.PropertyModeChange"/> on.
    /// With the flag off nothing can be scheduled and the endpoints answer 404, so there is nothing to apply: the schedule of
    /// an earlier deploy is removed, and a change already programmed waits until the flag is turned on again.
    /// </summary>
    private static void ConfigurePropertyModeJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(PropertyModeChangeJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<PropertyModeChangeJob>(
            PropertyModeChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyModeChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// RLI provider polling (LT-01): only with <see cref="FeatureFlags.RliProvider"/> on. With the flag off every lease is
    /// registered manually and there is nothing to poll; the schedule of an earlier deploy is removed.
    /// </summary>
    private static void ConfigureRliProviderJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(LeaseRegistrationStatusPollingJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<LeaseRegistrationStatusPollingJob>(
            LeaseRegistrationStatusPollingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// E-signature provider watch (LT-02): only with <see cref="FeatureFlags.ESignProvider"/> on. With the flag off every
    /// contract is signed offline and no lease waits for a provider; the schedule of an earlier deploy is removed.
    /// </summary>
    private static void ConfigureESignProviderJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(LeaseSignStatusPollingJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<LeaseSignStatusPollingJob>(
            LeaseSignStatusPollingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "*/10 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// OTA partner API jobs (D10, in freeze): only with <see cref="FeatureFlags.OtaPartnerApi"/> on. They still run with
    /// <see cref="Guid.Empty"/> (no property: a no-op with a warning, A9-15), to be rewritten before the flag is turned on.
    /// </summary>
    private static void ConfigureOtaPartnerJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OtaSyncAllJobId);
            recurringJobManager.RemoveIfExists(BookingPullAllJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OtaSyncJob>(
            OtaSyncAllJobId,
            job => job.ExecuteAsync(Guid.Empty),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<BookingPullJob>(
            BookingPullAllJobId,
            job => job.ExecuteAsync(Guid.Empty),
            "*/15 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
