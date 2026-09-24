using System.Globalization;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Host emails about the compliance status of a property (CO-06).</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string PropertyComplianceSuspended = "property-compliance-suspended";
    }

    /// <summary>
    /// Ids of the blocking steps of the activation (<see cref="IPropertyComplianceStatusService.GetBlockingStepsAsync"/>)
    /// and the EmailTexts key of their line in the suspension email.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SuspensionStepKeys = new Dictionary<string, string>
    {
        ["base-data"] = "PropertyComplianceSuspended_StepBaseData",
        ["cin"] = "PropertyComplianceSuspended_StepCin",
        ["documents"] = "PropertyComplianceSuspended_StepDocuments",
        ["safety"] = "PropertyComplianceSuspended_StepSafety",
    };

    /// <summary>
    /// The property was suspended from the booking site because an activation requirement is missing, to the host: one
    /// line per incomplete step (<paramref name="stepIds"/>), the confirmed bookings stay valid, link to the activation
    /// wizard where the details and the reactivation are.
    /// </summary>
    public static EmailContent PropertyComplianceSuspended(
        CultureInfo culture,
        string propertyName,
        IReadOnlyCollection<string> stepIds,
        string? activationUrl = null)
    {
        ArgumentNullException.ThrowIfNull(stepIds);
        var lines = SuspensionStepKeys
            .Where(step => stepIds.Contains(step.Key))
            .Select(step => (step.Value, Array.Empty<object?>()));

        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("PropertyComplianceSuspended_Body", propertyName)
            .List(lines)
            .Paragraph("PropertyComplianceSuspended_BookingsKept")
            .Paragraph("PropertyComplianceSuspended_Action");
        if (!string.IsNullOrWhiteSpace(activationUrl))
        {
            builder = builder
                .Button("PropertyComplianceSuspended_Cta", activationUrl)
                .LinkFallback("Booking_LinkFallback", activationUrl);
        }

        return builder.Build("PropertyComplianceSuspended_Subject", propertyName);
    }
}
