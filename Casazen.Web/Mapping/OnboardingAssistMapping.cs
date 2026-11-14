using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The guided panel steps and the «Do it for me» requests (NF-107, NF-15, E10/F01/US03/T01) as the API answers them: codes, ids and instants only.</summary>
public static class OnboardingAssistMapping
{
    public static PanelStepDto ToDto(this PanelStepView step) => Fill(new PanelStepDto(), step);

    public static StartedPanelStepDto ToDto(this PanelStepStart started)
    {
        var dto = Fill(new StartedPanelStepDto(), started.Step);
        dto.PanelUrl = started.PanelUrl;
        dto.CallbackUrl = started.CallbackUrl;
        return dto;
    }

    public static AssistRequestDto ToDto(this AssistRequestView request) => new()
    {
        Id = request.Id,
        Part = request.PartCode,
        PropertyId = request.PropertyId,
        Context = request.Context,
        ChannelCode = request.ChannelCode,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        UpdatedAt = request.UpdatedAt,
        CompletedAt = request.CompletedAt,
        StaffNote = request.StaffNote,
        ActivityId = request.ActivityId,
        Outcome = request.Outcome,
        HostNote = request.HostNote,
        ReopenedAt = request.ReopenedAt,
    };

    public static AdminAssistRequestDto ToDto(this AssistQueueItem item) => new()
    {
        Id = item.Id,
        OrgId = item.OrgId,
        Part = item.PartCode,
        PropertyId = item.PropertyId,
        PropertyName = item.PropertyName,
        Comune = item.Comune,
        Context = item.Context,
        ChannelCode = item.ChannelCode,
        Status = item.Status,
        RequestedAt = item.RequestedAt,
        UpdatedAt = item.UpdatedAt,
        CompletedAt = item.CompletedAt,
        AssignedToUserId = item.AssignedToUserId,
        StaffNote = item.StaffNote,
        DraftWizardType = item.DraftWizardType,
        DraftEntityId = item.DraftEntityId,
        Outcome = item.Outcome,
        HostNote = item.HostNote,
        ReopenedAt = item.ReopenedAt,
    };

    /// <summary>One request as staff reads it, with the checklist (E20/F03/US01/T02): presence, dates and codes only.</summary>
    public static AdminAssistRequestDetailDto ToDto(this AssistRequestDetail detail) => new()
    {
        Request = detail.Request.ToDto(),
        Checklist = [.. detail.Checklist.Select(block => block.ToDto())],
    };

    public static AssistChecklistPropertyDto ToDto(this AssistChecklistProperty block) => new()
    {
        PropertyId = block.PropertyId,
        PropertyName = block.PropertyName,
        Comune = block.Comune,
        PanelUser = new AssistPresenceDto { Present = block.PanelUser.Present, UpdatedAt = block.PanelUser.UpdatedAt },
        PoliceCredentials = block.PoliceCredentials is { } police
            ? new AssistPresenceDto { Present = police.Present, UpdatedAt = police.UpdatedAt }
            : null,
        IstatCredentials = block.IstatCredentials is { } istat
            ? new AssistIstatPresenceDto { Present = istat.Present, UpdatedAt = istat.UpdatedAt, Requirement = istat.Requirement }
            : null,
        Sender = block.Sender is { } sender
            ? new AssistSenderDto { Sender = sender.Sender, ChangedAt = sender.ChangedAt, ActivationDeclared = sender.ActivationDeclared }
            : null,
        Steps =
        [
            .. block.Steps.Select(step => new AssistChecklistStepDto
            {
                Kind = PanelStepCatalog.CodeOf(step.Kind),
                ChannelCode = step.ChannelCode,
                InstructionsKey = step.InstructionsKey,
                State = step.State,
                LastVerificationOutcome = step.LastVerificationOutcome,
                CompletedAt = step.CompletedAt,
            }),
        ],
    };

    public static PagedResultDto<AdminAssistRequestDto> ToDto(this AssistQueuePage page) => new()
    {
        Items = [.. page.Items.Select(item => item.ToDto())],
        TotalCount = page.TotalCount,
        Page = page.Page,
        PageSize = page.PageSize,
    };

    private static T Fill<T>(T dto, PanelStepView step)
        where T : PanelStepDto
    {
        dto.Id = step.Id;
        dto.Kind = PanelStepCatalog.CodeOf(step.Kind);
        dto.Status = PanelStepCatalog.CodeOf(step.Status);
        dto.HandoffMode = PanelStepCatalog.CodeOf(step.HandoffMode);
        dto.PropertyId = step.PropertyId;
        dto.ChannelCode = step.ChannelCode;
        dto.InstructionsKey = step.InstructionsKey;
        dto.ActivityId = step.ActivityId;
        dto.NextCheckAt = step.NextCheckAt;
        dto.LastVerificationOutcome = step.LastVerificationOutcome;
        dto.CanConfirm = step.CanConfirm;
        dto.Resume = new PanelStepResumeDto
        {
            WizardType = step.Resume.WizardType,
            DraftEntityId = step.Resume.DraftEntityId,
            StepCode = step.Resume.StepCode,
        };
        dto.StartedAt = step.StartedAt;
        dto.CompletedAt = step.CompletedAt;
        return dto;
    }
}
