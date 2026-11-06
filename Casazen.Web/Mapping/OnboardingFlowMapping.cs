using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The flow status (NF-106, E10/F01/US01/T02) as the API answers it: codes and counts only.</summary>
public static class OnboardingFlowMapping
{
    public static FlowSummaryDto ToDto(this FlowSummary summary) => new()
    {
        Flow = summary.Flow,
        Active = summary.Active,
        ToComplete = summary.ToComplete,
        NotAvailable = summary.NotAvailable,
        Total = summary.Total,
    };

    public static FlowPrerequisiteDto ToDto(this FlowPrerequisite prerequisite) => new()
    {
        Key = prerequisite.Key,
        Met = prerequisite.Met,
        Reason = prerequisite.Met ? null : prerequisite.Reason ?? OnboardingPrerequisiteKeys.DefaultReasonOf(prerequisite.Key),
    };

    public static FlowPropertyStatusDto ToDto(this FlowPropertyStatus status) => new()
    {
        PropertyId = status.PropertyId,
        Name = status.Name,
        City = status.City,
        Flow = status.Flow,
        State = status.State,
        Reason = status.Reason,
        Action = status.Action,
        Prerequisites = [.. status.Prerequisites.Select(prerequisite => prerequisite.ToDto())],
    };

    public static PagedResultDto<FlowPropertyStatusDto> ToDto(this FlowPropertyPage page) => new()
    {
        Items = page.Items.Select(status => status.ToDto()).ToList(),
        TotalCount = page.TotalCount,
        Page = page.Page,
        PageSize = page.PageSize,
    };
}
