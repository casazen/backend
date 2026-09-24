using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Every transactional email of the application, rendered with <see cref="EmailHtmlBuilder"/> in Italian (default)
/// or English. Links are passed in already built by <see cref="PublicSiteLinks"/>.
/// </summary>
public static class EmailTemplates
{
    /// <summary>Recipients have no language preference yet: emails are sent in Italian.</summary>
    public static CultureInfo DefaultCulture { get; } = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Template names used in logs and queued jobs.</summary>
    public static class Names
    {
        public const string ServiceRequestCreated = "service-request-created";
        public const string ServiceRequestStatusChanged = "service-request-status-changed";
        public const string SupplierInvite = "supplier-invite";
        public const string GuestCheckInLink = "guest-checkin-link";
        public const string GuestCheckInIncomplete = "guest-checkin-incomplete";
        public const string AlloggiatiDeadline = "alloggiati-deadline";
    }

    /// <summary>EmailTexts key of the label of a <see cref="ServiceCategories"/> code.</summary>
    public static string ServiceCategoryKey(string code) => $"ServiceCategory_{code}";

    /// <summary>
    /// Label of a service category code in <paramref name="culture"/> (SU-03). A value that is not a known code (an
    /// old value kept by the migration) is shown as it is; the builder HTML-encodes it like any dynamic value.
    /// </summary>
    public static string ServiceCategoryLabel(CultureInfo culture, string category) =>
        ServiceCategories.IsKnown(category)
            ? EmailTexts.Get(ServiceCategoryKey(category), culture)
            : category;

    /// <summary>New service request, to the supplier.</summary>
    public static EmailContent ServiceRequestCreated(
        CultureInfo culture,
        string supplierName,
        string category,
        string propertyName,
        string? notes,
        string inboxUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName)
            .Paragraph("ServiceRequestCreated_Body", ServiceCategoryLabel(culture, category), propertyName)
            .Quote("ServiceRequestCreated_NotesLabel", notes)
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestCreated_Subject", propertyName);

    /// <summary>Service request taken, completed or rejected by the supplier, to the host.</summary>
    public static EmailContent ServiceRequestStatusChanged(
        CultureInfo culture,
        ServiceRequestStatus status,
        string category,
        string propertyName,
        string? rejectionReason = null)
    {
        var prefix = status switch
        {
            ServiceRequestStatus.PresoInCarico => "ServiceRequestTaken",
            ServiceRequestStatus.Completato => "ServiceRequestCompleted",
            ServiceRequestStatus.Rifiutato => "ServiceRequestRejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No email for this service request status."),
        };

        var builder = new EmailHtmlBuilder(culture).Paragraph($"{prefix}_Body", ServiceCategoryLabel(culture, category), propertyName);
        if (status == ServiceRequestStatus.Rifiutato)
            builder.Quote("ServiceRequestRejected_ReasonLabel", rejectionReason);

        return builder.Build($"{prefix}_Subject", propertyName);
    }

    /// <summary>
    /// Invitation of a prospective supplier by a platform admin. <paramref name="comune"/> is the text shown for the
    /// comune ("Name (code)" when the name is known, else the code); the expiry is shown in Italian time (Europe/Rome).
    /// </summary>
    public static EmailContent SupplierInvite(
        CultureInfo culture,
        string email,
        string comune,
        string? message,
        string signupUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Heading("SupplierInvite_Title")
            .Paragraph("SupplierInvite_Body", comune)
            .Muted("SupplierInvite_EmailHint", email)
            .Paragraph("SupplierInvite_NextSteps")
            .Quote("SupplierInvite_MessageLabel", message)
            .Button("SupplierInvite_Cta", signupUrl)
            .LinkFallback("SupplierInvite_LinkFallback", signupUrl)
            .Muted("SupplierInvite_Expires", builder.FormatInstant(expiresAtUtc))
            .Build("SupplierInvite_Subject");
    }

    /// <summary>Tokenized self check-in link, to the guest.</summary>
    public static EmailContent GuestCheckInLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string checkInUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestCheckInLink_Greeting", guestName)
            .Paragraph("GuestCheckInLink_Body", propertyName, checkInDate)
            .Paragraph("GuestCheckInLink_Instructions")
            .Button("GuestCheckInLink_Cta", checkInUrl)
            .Muted("GuestCheckInLink_Validity")
            .Build("GuestCheckInLink_Subject", propertyName);

    /// <summary>Guest check-in still incomplete close to arrival, to the host.</summary>
    public static EmailContent GuestCheckInIncomplete(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestCheckInIncomplete_Body", guestName, propertyName, checkInDate)
            .Paragraph("GuestCheckInIncomplete_Action")
            .Build("GuestCheckInIncomplete_Subject", propertyName, checkInDate);

    /// <summary>Alloggiati Web report close to its deadline, to the host.</summary>
    public static EmailContent AlloggiatiDeadline(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("AlloggiatiDeadline_Body", guestName, propertyName, checkInDate)
            .Paragraph("AlloggiatiDeadline_Action")
            .Build("AlloggiatiDeadline_Subject", propertyName, checkInDate);
}
