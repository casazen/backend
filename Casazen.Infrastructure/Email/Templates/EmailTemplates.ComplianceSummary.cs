using System.Globalization;
using Casazen.Core.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The monthly compliance summary of an organization (E03/F02/US02/T02, NF-52, CHK-12 point 6): «October 2026: 42 stays, all compliant», or what is missing.
/// It holds counts, the names of the open actions and a link, never a guest name, a document, a contact or a booking code.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ComplianceMonthlySummary = "compliance-monthly-summary";
    }

    /// <summary>
    /// The summary of one month, to the host. Three texts: no stay and nothing owed; every applicable obligation with its proof («all compliant», said only when the
    /// report says so); otherwise what is not settled, grouped by the action that fixes it, the count of the obligations by state, and a word on the ones held only by
    /// the channel manager (CasaZen does not see their outcome and never gives them for done). The button leads to the compliance page of the console when its
    /// link is known.
    /// </summary>
    public static EmailContent ComplianceSummary(CultureInfo culture, ComplianceMonthSummary summary, string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var month = MonthLabel(summary.YearMonth, culture);
        var builder = new EmailHtmlBuilder(culture);
        string subjectKey;
        object?[] subjectArgs;

        if (summary.Complete && summary.Stays == 0 && summary.Counts.OfficialProof == 0)
        {
            subjectKey = "ComplianceSummary_Subject_NoStays";
            subjectArgs = [month];
            builder = builder.Paragraph("ComplianceSummary_Body_NoStays", month);
        }
        else if (summary.Complete)
        {
            subjectKey = "ComplianceSummary_Subject_AllCompliant";
            subjectArgs = [month, summary.Stays];
            builder = builder.Paragraph("ComplianceSummary_Body_AllCompliant", month, summary.Stays);
        }
        else
        {
            subjectKey = "ComplianceSummary_Subject_Open";
            subjectArgs = [month, summary.Stays, summary.NotSettled];
            builder = builder.Paragraph("ComplianceSummary_Body_Open", month, summary.Stays);

            // What has no proof, by the action that fixes it: counts and names of actions, never who or which stay.
            builder = builder.List(summary.OpenItems
                .Where(item => item.EvidenceClass == ComplianceEvidenceClass.Missing)
                .GroupBy(item => ItemKeyOf(item.Action))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (group.Key, new object?[] { group.Count() })));
            if (summary.Counts.ExternalVerificationRequired > 0)
                builder = builder.Paragraph("ComplianceSummary_ExternalVerification");
        }

        if (summary.Counts.OfficialProof + summary.Counts.ExternalVerificationRequired + summary.Counts.Missing + summary.Counts.NotApplicable > 0)
        {
            builder = builder
                .Muted("ComplianceSummary_Totals")
                .List(new (string Key, object?[] Args)[]
                {
                    ("ComplianceSummary_State_OfficialProof", new object?[] { summary.Counts.OfficialProof }),
                    ("ComplianceSummary_State_ExternalVerificationRequired", new object?[] { summary.Counts.ExternalVerificationRequired }),
                    ("ComplianceSummary_State_Missing", new object?[] { summary.Counts.Missing }),
                    ("ComplianceSummary_State_NotApplicable", new object?[] { summary.Counts.NotApplicable }),
                });
        }

        if (!string.IsNullOrWhiteSpace(complianceUrl))
            builder = builder.Button("ComplianceAlert_Cta", complianceUrl).LinkFallback("Booking_LinkFallback", complianceUrl);

        return builder.Build(subjectKey, subjectArgs);
    }

    /// <summary>The key of the line that names the action that fixes an open item; one fixed text for each action the summary can hold, and a general one for any other.</summary>
    private static string ItemKeyOf(ComplianceCockpitAction? action) => action switch
    {
        ComplianceCockpitAction.SendAlloggiati => "ComplianceSummary_Item_SendAlloggiati",
        ComplianceCockpitAction.ResolveAlloggiatiFailure => "ComplianceSummary_Item_ResolveAlloggiatiFailure",
        ComplianceCockpitAction.UploadAlloggiatiReceipt => "ComplianceSummary_Item_UploadAlloggiatiReceipt",
        ComplianceCockpitAction.SendAlloggiatiManually => "ComplianceSummary_Item_SendAlloggiatiManually",
        ComplianceCockpitAction.UploadIstatProof => "ComplianceSummary_Item_UploadIstatProof",
        ComplianceCockpitAction.DeclareTouristTax => "ComplianceSummary_Item_DeclareTouristTax",
        _ => "ComplianceSummary_Item_Other",
    };
}
