using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Jobs;
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

    /// <param name="recurringJobManager">The Hangfire schedule of the environment.</param>
    /// <param name="featureFlags">The flags that decide the jobs behind a feature.</param>
    /// <param name="routineOptions">The interval of the time triggers of the routines (section <c>Routine</c>); the defaults when omitted.</param>
    public static void Configure(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags, RoutineOptions? routineOptions = null)
    {
        ConfigureOtaPartnerJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OtaPartnerApi));

        // E02/F02: the jobs of the Octorate connector (bookings and calendar), only while the connector is on.
        ConfigureOctorateJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        // E10/F01/US01/T04: the "To do" of the flows to complete and of a lost connection, only while the activation with Octorate is on.
        ConfigureOnboardingJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateOnboarding));

        // ADR-022-R8: the time triggers of the stays ("2 days before check-in") are written to the outbox every
        // Routine:EvaluationIntervalMinutes (default 5), whatever the flags say; an org without a time routine gets nothing.
        recurringJobManager.AddOrUpdate<TimeEventEmitterJob>(
            TimeEventEmitterJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            (routineOptions ?? new RoutineOptions()).EmitterCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // ADR-022-R4: the outbox dispatcher publishes the recorded domain events on the bus every minute, whatever the flags say.
        recurringJobManager.AddOrUpdate<OutboxDispatcherJob>(
            OutboxDispatcherJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OutboxDispatcherJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // ADR-022-R15: the dispatched events older than EventBus:RetentionDays are deleted and the old dead letters anonymized, nightly.
        recurringJobManager.AddOrUpdate<OutboxCleanupJob>(
            OutboxCleanupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OutboxCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E07/F01/US02/T03 (NF-88, NF-20): the CRM profiles and contacts of the bookers of every booking changed in the last two hours, and the
        // guests with no profile yet (200 a run, until none is left), every 15 minutes. It guarantees what the event handler only hastens, and
        // does nothing, with one line in the log, unless Features:CrmMarketing is on and Crm:ContactIndexKey is configured (read at every run).
        // Always registered: nothing to remove when the module is closed.
        recurringJobManager.AddOrUpdate<CrmProfileSyncJob>(
            CrmProfileSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CrmProfileSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // ADR-025-R3, R6: the popup alerts held back by the quiet hours of a user, or snoozed by the user, are pushed to its open sessions
        // when they fall due, every minute, whatever the flags say (a user with the app open all night would never see them otherwise).
        recurringJobManager.AddOrUpdate<PopupAlertDueJob>(
            PopupAlertDueJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PopupAlertDueJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureServiceRequestAutoCancel(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.SupplierRequestAutoCancel));

        // E09/F01/US01/T02 (ADR-023): the cache of the portal catalog of the marketplace is refreshed every day, only while the Octorate connector is on.
        ConfigureMarketplacePortalRefresh(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        // SP-BKP: the groups of backup suppliers are followed whatever the flag SupplierRequestBackups says (it only gates their creation): a
        // group that exists has its windows, and turning the flag off must not leave its requests hanging. With no group a run finds nothing.
        recurringJobManager.AddOrUpdate<ServiceRequestGroupJob>(
            ServiceRequestGroupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestGroupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

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

        ConfigureServicePayments(recurringJobManager);

        // SP-13b: the images of the showcases that neither the draft, nor the published version, nor the one before it point to are removed
        // after a day, every night. Always registered: a file that exists costs money whatever a flag says; with none to remove a run finds nothing.
        recurringJobManager.AddOrUpdate<SupplierShowcaseMediaCleanupJob>(
            SupplierShowcaseMediaCleanupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SupplierShowcaseMediaCleanupJob.Cron,
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

        // AM-02: reminder of the third day, expiry and deletion of the closed org invitations.
        recurringJobManager.AddOrUpdate<OrgInvitationMaintenanceJob>(
            OrgInvitationMaintenanceJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgInvitationMaintenanceJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Host invites: the email of a closed invite is erased after HostInvites:RetentionDays.
        recurringJobManager.AddOrUpdate<HostInviteRetentionJob>(
            HostInviteRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            HostInviteRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // AM-02b: the activity log keeps 12 months.
        recurringJobManager.AddOrUpdate<OrgActivityRetentionJob>(
            OrgActivityRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgActivityRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureESignProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ESignProvider));

        ConfigureRliProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.RliProvider));

        // PM-02: application of the scheduled changes of rental mode, every hour.
        recurringJobManager.AddOrUpdate<PropertyModeChangeJob>(
            PropertyModeChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyModeChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-12: the abandoned lease drafts (no change for LongRentLease:DraftRetentionDays) are deleted every night.
        recurringJobManager.AddOrUpdate<LeaseDraftRetentionJob>(
            LeaseDraftRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            LeaseDraftRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-14: the leases whose period is over and that nobody gave notice of renew by themselves, every hour.
        recurringJobManager.AddOrUpdate<LeaseTermRenewalJob>(
            LeaseTermRenewalJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            LeaseTermRenewalJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-11c-a: the landlord is reminded of the last day to give notice of a lease (30 and 7 days before, and that day), every day.
        recurringJobManager.AddOrUpdate<LeaseNoticeReminderJob>(
            LeaseNoticeReminderJob.RecurringJobId,
            job => job.ExecuteAsync(),
            LeaseNoticeReminderJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

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

        // SR-10b: the page of the calendars says how often this runs from the same constant, never from a number of its own.
        recurringJobManager.AddOrUpdate<PropertyICalSyncJob>(
            "property-ical-sync",
            job => job.ExecuteAsync(),
            PropertyICalSchedule.SyncCron,
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

        // UI-12a: the in-app notifications older than 90 days are deleted every night.
        recurringJobManager.AddOrUpdate<InAppNotificationRetentionJob>(
            InAppNotificationRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            InAppNotificationRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // NF-06: approval queue items pending for >7 days are expired every night.
        recurringJobManager.AddOrUpdate<ApprovalExpirationJob>(
            ApprovalExpirationJob.RecurringJobId,
            job => job.ProcessExpirationsAsync(CancellationToken.None),
            ApprovalExpirationJob.Cron,
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

        // RS-6, RS-7, CO-12: daily check of the official ISTAT comuni file, the Alloggiati tables and the tourist-tax pages.
        recurringJobManager.AddOrUpdate<OfficialReferenceDataRefreshJob>(
            OfficialReferenceDataRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OfficialReferenceDataRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // MEF nuova_at profiles of comuni that already have a house: monthly delta, never a full Italy scrape.
        recurringJobManager.AddOrUpdate<ComuneOfficialProfileRefreshJob>(
            ComuneOfficialProfileRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(),
            ComuneOfficialProfileRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The jobs of the Octorate connector (epic E02): only with <see cref="FeatureFlags.OctorateConnector"/> on. With the flag off no booking is
    /// read and no calendar is synced, and the schedule of an earlier deploy is removed (<c>RemoveIfExists</c>), as for the OTA partner jobs.
    /// <list type="bullet">
    /// <item>the daily import of the bookings, at 02:00 Europe/Rome (E02/F02/US01/T01, ADR-033-R4);</item>
    /// <item>the reconciliation of the bookings every six hours (E02/F02/US01/T02, ADR-031-R8);</item>
    /// <item>the sync of the calendar every 15 minutes (E02/F02/US01/T03, ADR-002-R2, NF-36), next to the iCal sync of the feeds
    /// (<c>property-ical-sync</c>), which stays registered as it is;</item>
    /// <item>the retry of the check-in writes (guests and states of the stay) every 15 minutes (E03/F01/US01/T03);</item>
    /// <item>the pass over the OTA conversations every 10 minutes and the deep pass at 02:30 UTC (epic E05, E05/F01/US01/T03, ADR-032, OCT-09).</item>
    /// </list>
    /// </summary>
    private static void ConfigureOctorateJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OctorateBookingImportJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OctorateBookingSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(CalendarSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OctorateCheckInRetryJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OtaMessageReconcileJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OtaMessageDeepReconcileJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingStatusSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingChannelHealthJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingOutsideChangeJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(CalendarChangeProcessJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OctorateCheckInRetryJob>(
            OctorateCheckInRetryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateCheckInRetryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OctorateBookingImportJob>(
            OctorateBookingImportJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateBookingImportJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });

        recurringJobManager.AddOrUpdate<OctorateBookingSyncJob>(
            OctorateBookingSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateBookingSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CalendarSyncJob>(
            CalendarSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CalendarSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E05/F01/US01/T03 (NF-62, OCT-09): the copy of the OTA conversations, kept up to date by the webhook and recovered by these two passes.
        recurringJobManager.AddOrUpdate<OtaMessageReconcileJob>(
            OtaMessageReconcileJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OtaMessageReconcileJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OtaMessageDeepReconcileJob>(
            OtaMessageDeepReconcileJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OtaMessageDeepReconcileJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E02/F03/US01/T07: the follow-up of the publications of the listings (every 15 minutes), the channels that went silent (daily) and the
        // changes made on Octorate outside CasaZen (hourly). None of them writes to Octorate but the sending of the photos a run left unsent and the removal of a photo that left the gallery.
        recurringJobManager.AddOrUpdate<ListingStatusSyncJob>(
            ListingStatusSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingStatusSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ListingChannelHealthJob>(
            ListingChannelHealthJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingChannelHealthJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ListingOutsideChangeJob>(
            ListingOutsideChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingOutsideChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E08/F05/US02/T02 (NF-36, NF-78): the changes of the calendar that are due (a postponement for quota, a back-off, a retry) are sent, and what the channel manager
        // accepted is followed until it confirms it, every minute; the run enqueued right after a change is the fast path and this is the safety net.
        recurringJobManager.AddOrUpdate<CalendarChangeProcessJob>(
            CalendarChangeProcessJob.RecurringJobId,
            job => job.RunDueAsync(CancellationToken.None),
            CalendarChangeProcessJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The "To do" sync of the activation of CasaZen with Octorate (E10/F01/US01/T04, NF-106, NF-108): only with
    /// <see cref="FeatureFlags.OctorateOnboarding"/> on, hourly at minute 20 UTC. With the flag off no "To do" of a flow is raised or closed and the
    /// schedule of an earlier deploy is removed (<c>RemoveIfExists</c>).
    /// </summary>
    private static void ConfigureOnboardingJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OnboardingFlowTodoSyncJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OnboardingFlowTodoSyncJob>(
            OnboardingFlowTodoSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OnboardingFlowTodoSyncJob.Cron,
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
    /// The daily refresh of the portal catalog cached for the marketplace (E09/F01/US01/T02): only with
    /// <see cref="FeatureFlags.OctorateConnector"/> on. With the flag off nothing calls Octorate, and the schedule of an earlier deploy is
    /// removed; the cache keeps what it had, and the marketplace goes on reading it.
    /// </summary>
    private static void ConfigureMarketplacePortalRefresh(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(MarketplacePortalRefreshJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<MarketplacePortalRefreshJob>(
            MarketplacePortalRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            MarketplacePortalRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The jobs of the supplier service payments (SP-15b), <b>always</b> registered, whatever the state of
    /// <see cref="FeatureFlags.SupplierOnlinePayments"/>: payments that exist (money in flight) must be followed to the end. The sync
    /// reads the payments Stripe is processing and the refunds it has not completed; the reminders flag the late payments and, with
    /// the flag on, send the pending requests and the reminders. The flag is read by the jobs themselves.
    /// </summary>
    private static void ConfigureServicePayments(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<ServicePaymentSyncJob>(
            ServicePaymentSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServicePaymentSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ServicePaymentRemindersJob>(
            ServicePaymentRemindersJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServicePaymentRemindersJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // The money CasaZen holds (SP-15c-a): releases, retries of failed transfers, closed requests, lapsed blocks, hold warnings.
        recurringJobManager.AddOrUpdate<ServiceFundsJob>(
            ServiceFundsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceFundsJob.Cron,
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
