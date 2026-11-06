using Casazen.Core.Services;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

/// <summary>The integration settings and the reopen of a part (NF-108, E10/F01/US02/T01) as the API answers them: codes, counts and instants only.</summary>
public static class OnboardingIntegrationMapping
{
    public static OnboardingIntegrationDto ToDto(this OnboardingIntegrationOverview overview) => new()
    {
        Connection = overview.Connection.ToDto(),
        Parts = [.. overview.Parts.Select(part => part.ToDto())],
        Senders = overview.Senders.ToDto(),
        Channels = [.. overview.Channels.Select(count => count.ToDto())],
    };

    public static OnboardingConnectionHealthDto ToDto(this OnboardingConnectionHealth health) => new()
    {
        Status = health.Status,
        Environment = health.Environment,
        LastSyncedAt = health.LastSyncedAt,
        ReconnectRequired = health.ReconnectRequired,
    };

    public static OnboardingIntegrationPartDto ToDto(this OnboardingIntegrationPart part)
    {
        // The plan part as the plan endpoint gives it, so the two answers can never say different things about a part.
        var plan = part.Plan.ToDto();
        return new OnboardingIntegrationPartDto
        {
            Code = plan.Code,
            Order = plan.Order,
            State = plan.State,
            Reason = plan.Reason,
            Done = plan.Done,
            Total = plan.Total,
            Requires = plan.Requires,
            Deferrable = plan.Deferrable,
            Needs = plan.Needs,
            RemindAt = plan.RemindAt,
            Assist = plan.Assist,
            LaunchTarget = plan.LaunchTarget,
            Reopenable = part.Reopenable,
        };
    }

    public static OnboardingSenderCountsDto ToDto(this OnboardingSenderCounts counts) => new()
    {
        ChannelManager = counts.ChannelManager,
        CasaZen = counts.CasaZen,
        Manual = counts.Manual,
        NotSet = counts.NotSet,
    };

    public static OnboardingChannelStatusCountDto ToDto(this OnboardingChannelStatusCount count) => new()
    {
        Status = count.Status,
        Count = count.Count,
    };

    public static OnboardingReopenDto ToDto(this OnboardingPartReopen reopen) => new()
    {
        Code = reopen.Code,
        LaunchTarget = reopen.LaunchTarget,
        Focus = reopen.Focus,
    };
}
