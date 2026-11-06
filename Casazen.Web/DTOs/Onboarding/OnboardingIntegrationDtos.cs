using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// The integration settings of the caller's org (<c>GET api/onboarding/integration</c>, NF-108, ONB-05): the connection to Octorate, the parts of
/// the plan with whether each can be reopened, the properties for each sender of the Alloggiati and ISTAT communications, and the channel states
/// for each publication status. Codes, counts and instants only: never a token, its ciphertext, a property or a person.
/// </summary>
public sealed class OnboardingIntegrationDto
{
    public OnboardingConnectionHealthDto Connection { get; set; } = new();

    /// <summary>The eleven parts of the plan in order, each as <c>GET api/onboarding/plan</c> gives it, plus <c>reopenable</c>.</summary>
    public List<OnboardingIntegrationPartDto> Parts { get; set; } = [];

    public OnboardingSenderCountsDto Senders { get; set; } = new();

    /// <summary>Every publication status, in order, with how many channel states are in it (zero when none).</summary>
    public List<OnboardingChannelStatusCountDto> Channels { get; set; } = [];
}

/// <summary>
/// The org's connection to Octorate in this environment. <c>status</c>: <c>Connected</c>, <c>Disconnected</c>, <c>Error</c>, or <c>null</c> when
/// the org never connected here. <c>reconnectRequired</c> is true when the status is <c>Disconnected</c> or <c>Error</c> (a connection never
/// made is a connect, not a reconnect).
/// </summary>
public sealed class OnboardingConnectionHealthDto
{
    public OctorateConnectionStatus? Status { get; set; }

    public OctorateEnvironment Environment { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public bool ReconnectRequired { get; set; }
}

/// <summary>
/// A part of the plan as <see cref="OnboardingPlanPartDto"/> gives it, plus <c>reopenable</c>: false for a part nobody provides
/// (<c>state</c> <c>notAvailable</c>), which has no wizard to go back to.
/// </summary>
public sealed class OnboardingIntegrationPartDto
{
    public string Code { get; set; } = string.Empty;

    public int Order { get; set; }

    public string State { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public List<string> Requires { get; set; } = [];

    public bool Deferrable { get; set; }

    public List<string> Needs { get; set; } = [];

    /// <summary>When the "To do" of a deferral or an exit is due (UTC); <c>null</c> when there is none.</summary>
    public DateTime? RemindAt { get; set; }

    /// <summary>The open «Do it for me» request of the part, if any.</summary>
    public OnboardingAssistDto? Assist { get; set; }

    public string LaunchTarget { get; set; } = string.Empty;

    public bool Reopenable { get; set; }
}

/// <summary>
/// The properties the caller reaches for each sender of the Alloggiati and ISTAT communications. <c>notSet</c>: no stored choice yet. A property
/// outside Italy has no sender to choose and is in no count.
/// </summary>
public sealed class OnboardingSenderCountsDto
{
    public int ChannelManager { get; set; }

    public int CasaZen { get; set; }

    public int Manual { get; set; }

    public int NotSet { get; set; }
}

/// <summary>How many channel states are in one publication status: <c>Draft</c>, <c>Sending</c>, <c>Accepted</c>, <c>Published</c>, <c>ToFix</c>, <c>ManagedOnChannel</c>.</summary>
public sealed class OnboardingChannelStatusCountDto
{
    public ChannelPublishStatus Status { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// Where a reopen continues (<c>POST api/onboarding/parts/{key}/reopen</c>): <c>launchTarget</c>, the wizard that does the part
/// (<c>connect</c>, <c>mapping</c>, <c>channels</c>, <c>listings</c>, <c>prices</c>, <c>compliance</c>, <c>identification</c>, <c>messages</c>,
/// <c>fiscal</c>, <c>marketplace</c>, <c>summary</c>), and <c>focus</c>: <c>sender</c> for the compliance wizard, which opens on the choice of the
/// sender; <c>null</c> when the launch target alone says where to continue. Reopening changes no data.
/// </summary>
public sealed class OnboardingReopenDto
{
    public string Code { get; set; } = string.Empty;

    public string LaunchTarget { get; set; } = string.Empty;

    public string? Focus { get; set; }
}
