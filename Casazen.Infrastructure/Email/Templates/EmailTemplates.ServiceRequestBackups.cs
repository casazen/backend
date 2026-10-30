using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Why a backup supplier's request was closed (SP-BKP): the wording of <see cref="EmailTemplates.ServiceRequestBackupClosed"/>.</summary>
public enum ServiceRequestBackupClosedKind
{
    /// <summary>The primary supplier took the job.</summary>
    PrimaryTookIt = 0,

    /// <summary>Another backup took the job first, in the second window.</summary>
    TakenByBackup = 1,

    /// <summary>The host cancelled the request.</summary>
    RequestCancelledByHost = 2,

    /// <summary>The backup had accepted, but when the job came to it its time was no longer free.</summary>
    SlotUnavailable = 3,

    /// <summary>Another backup accepted first, which closed the race for the others: no penalty.</summary>
    RaceClosed = 4,

    /// <summary>The backup had accepted and the primary supplier took the job after all: the backup is released, its time is free again, no penalty.</summary>
    ReleasedByPrimary = 5,
}

public static partial class EmailTemplates
{
    /// <summary>Template names used in logs and queued jobs (SP-BKP).</summary>
    public static partial class Names
    {
        public const string ServiceRequestBackupInvited = "service-request-backup-invited";
        public const string ServiceRequestBackupActive = "service-request-backup-active";
        public const string ServiceRequestBackupWon = "service-request-backup-won";
        public const string ServiceRequestBackupClosed = "service-request-backup-closed";
        public const string ServiceRequestTakenByBackup = "service-request-taken-by-backup";
    }

    /// <summary>
    /// A request also sent to this supplier as a <b>backup</b> (SP-BKP), to the supplier: it says plainly that the job is its only if the
    /// primary supplier does not take it by <paramref name="primaryDueAtUtc"/>, and that accepting now (as a backup) puts it first in
    /// line. Like the email of a new request it names the comune, the time and the amount, never the property or the host's notes
    /// (decision D9).
    /// </summary>
    public static EmailContent ServiceRequestBackupInvited(
        CultureInfo culture,
        string supplierName,
        string category,
        string comune,
        string inboxUrl,
        DateTime primaryDueAtUtc,
        DateTime? scheduledStartUtc = null,
        int? amountCents = null)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName);
        builder.Paragraph("ServiceRequestBackupInvited_Body", ServiceCategoryLabel(culture, category), comune, builder.FormatInstant(primaryDueAtUtc));
        if (scheduledStartUtc is { } scheduledStart)
            builder.Paragraph("ServiceRequestCreated_When", builder.FormatInstant(scheduledStart));
        if (amountCents is { } amount)
            builder.Paragraph("ServiceRequestCreated_Price", FormatEuro(amount, culture));

        return builder
            .Muted("ServiceRequestBackupInvited_Hint")
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestBackupInvited_Subject", comune);
    }

    /// <summary>
    /// The second window of a group opened (SP-BKP), to a backup that has not taken the job: the primary supplier did not take it in time
    /// (or refused it), so the backup can take it now, until <paramref name="backupDueAtUtc"/>, and the first to do so has it.
    /// </summary>
    public static EmailContent ServiceRequestBackupActive(
        CultureInfo culture,
        string supplierName,
        string category,
        string comune,
        string inboxUrl,
        DateTime backupDueAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName);
        builder.Paragraph("ServiceRequestBackupActive_Body", ServiceCategoryLabel(culture, category), comune, builder.FormatInstant(backupDueAtUtc));
        return builder
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestBackupActive_Subject", comune);
    }

    /// <summary>
    /// The job went to this backup (SP-BKP), which had accepted: the primary supplier did not take it in time. To the supplier, who did not
    /// act at that moment. The address and the host's contact are in the console, as after any take, and are not written here.
    /// </summary>
    public static EmailContent ServiceRequestBackupWon(
        CultureInfo culture,
        string supplierName,
        string category,
        string comune,
        string inboxUrl,
        DateTime? scheduledStartUtc = null,
        int? amountCents = null)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName)
            .Paragraph("ServiceRequestBackupWon_Body", ServiceCategoryLabel(culture, category), comune);
        if (scheduledStartUtc is { } scheduledStart)
            builder.Paragraph("ServiceRequestCreated_When", builder.FormatInstant(scheduledStart));
        if (amountCents is { } amount)
            builder.Paragraph("ServiceRequestCreated_Price", FormatEuro(amount, culture));

        return builder
            .Muted("ServiceRequestBackupWon_Hint")
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestBackupWon_Subject", comune);
    }

    /// <summary>
    /// A backup supplier's request was closed (SP-BKP), to the supplier: the job went to the primary or to another backup, the host
    /// cancelled, or the backup's time was no longer free. It names the comune only and never the host's words.
    /// </summary>
    public static EmailContent ServiceRequestBackupClosed(
        CultureInfo culture,
        string supplierName,
        string category,
        string comune,
        ServiceRequestBackupClosedKind kind,
        string inboxUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName)
            .Paragraph($"ServiceRequestBackupClosed_{kind}_Body", ServiceCategoryLabel(culture, category), comune)
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestBackupClosed_Subject", comune);

    /// <summary>
    /// The primary supplier did not take the request in time and a backup that had been told so did (SP-BKP), to the host: who has the
    /// job now. <paramref name="hostUrl"/> is the page of the stay when the request has one (a long-rent request has none).
    /// </summary>
    public static EmailContent ServiceRequestTakenByBackup(
        CultureInfo culture,
        string category,
        string propertyName,
        string supplierName,
        DateTime? scheduledStartUtc,
        string? hostUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("ServiceRequestTakenByBackup_Body", ServiceCategoryLabel(culture, category), propertyName, supplierName);
        if (scheduledStartUtc is { } scheduledStart)
            builder.Paragraph("ServiceRequestTaken_When", builder.FormatInstant(scheduledStart));
        builder.Muted("ServiceRequestTakenByBackup_Hint");
        if (hostUrl is not null)
            builder.Button("ServiceRequestTimeProposed_Cta", hostUrl);

        return builder.Build("ServiceRequestTakenByBackup_Subject", propertyName);
    }

    // ─── Push ───

    /// <summary>A request also sent to this supplier as a backup (SP-BKP), to the supplier: the comune, not the property.</summary>
    public static PushText ServiceRequestBackupInvitedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupInvited", ServiceCategoryLabel(culture, category), comune);

    /// <summary>The second window opened (SP-BKP), to a backup that can take the job now.</summary>
    public static PushText ServiceRequestBackupActivePush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupActive", ServiceCategoryLabel(culture, category), comune);

    /// <summary>The job went to this backup (SP-BKP).</summary>
    public static PushText ServiceRequestBackupWonPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupWon", ServiceCategoryLabel(culture, category), comune);

    /// <summary>A backup supplier's request was closed (SP-BKP).</summary>
    public static PushText ServiceRequestBackupClosedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupClosed", ServiceCategoryLabel(culture, category), comune);

    /// <summary>A backup took the job the primary supplier did not take in time (SP-BKP), to the host.</summary>
    public static PushText ServiceRequestTakenByBackupPush(CultureInfo culture, string category, string propertyName, string supplierName) =>
        Push(culture, "Push_ServiceRequestTakenByBackup", ServiceCategoryLabel(culture, category), propertyName, supplierName);
}
