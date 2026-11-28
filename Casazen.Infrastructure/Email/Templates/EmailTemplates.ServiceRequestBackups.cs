using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public enum ServiceRequestBackupClosedKind
{
    PrimaryTookIt = 0,

    TakenByBackup = 1,

    RequestCancelledByHost = 2,

    SlotUnavailable = 3,

    /// <summary>Another backup accepted first, which closed the race for the others: no penalty.</summary>
    RaceClosed = 4,

    ReleasedByPrimary = 5,
}

public static partial class EmailTemplates
{
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

    public static PushText ServiceRequestBackupInvitedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupInvited", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestBackupActivePush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupActive", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestBackupWonPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupWon", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestBackupClosedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestBackupClosed", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestTakenByBackupPush(CultureInfo culture, string category, string propertyName, string supplierName) =>
        Push(culture, "Push_ServiceRequestTakenByBackup", ServiceCategoryLabel(culture, category), propertyName, supplierName);
}
