using Casazen.Core.Services;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The bulk configuration (E10/F02/US01/T01) as the API takes and answers it: ids, counts and codes only.</summary>
public static class OnboardingBulkMapping
{
    public static BulkSelection ToModel(this BulkSelectionDto? dto) => new(
        dto?.Kind ?? string.Empty,
        dto?.PropertyIds,
        dto?.Filter is { } filter ? filter.ToModel() : null);

    public static BulkChoice ToModel(this BulkChoiceDto? dto) => new(dto?.ComplianceSender);

    public static BulkPreviewDto ToDto(this BulkPreview preview) => new()
    {
        Total = preview.Total,
        Sample = [.. preview.Sample.Select(p => new BulkPropertySampleDto { PropertyId = p.PropertyId, Name = p.Name, City = p.City })],
        NotFound = preview.NotFound,
        ExceedsLimit = preview.ExceedsLimit,
        MaxPropertiesPerCall = preview.MaxPropertiesPerCall,
    };

    public static BulkApplyResultDto ToDto(this BulkApplyResult result) => new()
    {
        DryRun = result.DryRun,
        ComplianceSender = result.ComplianceSender,
        Summary = new BulkApplySummaryDto
        {
            Applied = result.Summary.Applied,
            Unchanged = result.Summary.Unchanged,
            Refused = result.Summary.Refused,
            WouldChange = result.Summary.WouldChange,
        },
        Items =
        [
            .. result.Items.Select(item => new BulkApplyItemDto
            {
                PropertyId = item.PropertyId,
                Name = item.Name,
                City = item.City,
                Outcome = item.Outcome,
                Reason = item.Reason,
            }),
        ],
    };

    public static BulkProgressDto ToDto(this BulkProgress progress) => new()
    {
        Total = progress.Total,
        Ready = progress.Ready,
        NotReady = progress.NotReady,
        Flows =
        [
            .. progress.Flows.Select(flow => new BulkFlowProgressDto
            {
                Flow = flow.Flow,
                Active = flow.Active,
                Ready = flow.Ready,
                ToComplete = flow.ToComplete,
                NotAvailable = flow.NotAvailable,
            }),
        ],
        Pilot = progress.Pilot.ToDto(),
    };

    public static BulkPilotDto ToDto(this BulkPilot pilot) => new()
    {
        Members =
        [
            .. pilot.Members.Select(member => new BulkPilotMemberDto
            {
                PropertyId = member.PropertyId,
                Name = member.Name,
                City = member.City,
                AddedAt = member.AddedAt,
            }),
        ],
        Size = pilot.Size,
        RecommendedMin = pilot.RecommendedMin,
        MaxSize = pilot.MaxSize,
        BelowRecommended = pilot.BelowRecommended,
        Flows =
        [
            .. pilot.Flows.Select(flow => new BulkPilotFlowDto
            {
                Flow = flow.Flow,
                Available = flow.Available,
                Active = flow.Active,
                IsActive = flow.IsActive,
            }),
        ],
    };

    public static BulkActivateResultDto ToDto(this BulkActivationResult result) => new()
    {
        Cohort = result.Cohort,
        Flows =
        [
            .. result.Flows.Select(flow => new BulkActivateFlowDto
            {
                Flow = flow.Flow,
                Outcomes = [.. flow.Outcomes.Select(OutcomeToDto)],
            }),
        ],
    };

    private static BulkFilter ToModel(this BulkFilterDto dto) => new(
        dto.ComuneIstatCodes,
        dto.RegionCodes,
        dto.ChannelCodes,
        dto.MissingFlow,
        dto.FlowState is { } state
            // A state without its flow is a body that cannot be read: the service refuses it as an invalid selection.
            ? new BulkFlowStateFilter(state.Flow ?? default, state.State ?? string.Empty)
            : null);

    private static FlowActivationOutcomeDto OutcomeToDto(FlowActivationOutcome outcome) => new()
    {
        PropertyId = outcome.PropertyId,
        Outcome = outcome.Outcome,
        UnmetPrerequisites = [.. outcome.UnmetPrerequisites],
        Reason = outcome.Reason,
    };
}
