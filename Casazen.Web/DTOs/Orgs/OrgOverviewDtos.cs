using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

// The overview of the org for the home of the account (AM-02c). A section the caller has no permission for is left out of the
// answer, not sent empty. Counts only, plus the ids of the latest activity: no name, no email.

/// <summary><c>GET /api/orgs/me/overview</c>: what the home of the account shows, in one answer.</summary>
public class OrgOverviewResponse
{
    /// <summary><c>ok</c>, or <c>attention</c> while a payment of the plan failed (<c>PastDue</c>), the subscription is <c>Unpaid</c> or <c>Incomplete</c>. Needs <c>org.billing.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Standing { get; set; }

    /// <summary>The org is on a plan higher than the one it pays for because the open access is on (the same flag as <c>GET /api/orgs/me/entitlement</c>). Needs <c>org.billing.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenAccess { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewSeatsDto? Seats { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewPropertiesDto? Properties { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewInvitationsDto? Invitations { get; set; }

    /// <summary>Requests for access waiting for an answer: 0 until the queue of the requests exists.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewAccessRequestsDto? AccessRequests { get; set; }

    /// <summary>Trusted suppliers: 0 until they exist. Needs <c>org.suppliers.manage</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewSuppliersDto? Suppliers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewAlloggiatiDto? Alloggiati { get; set; }

    /// <summary>The latest three things people did (never the system's), newest first. Needs <c>org.activity.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OrgActivityEntryDto>? RecentActivity { get; set; }

    public static OrgOverviewResponse From(OrgOverview overview) => new()
    {
        Standing = overview.Standing,
        OpenAccess = overview.OpenAccess,
        Seats = overview.Seats is { } seats ? new OrgOverviewSeatsDto { Used = seats.Used, Max = seats.Max, Unlimited = seats.Unlimited } : null,
        Properties = overview.Properties is { } properties
            ? new OrgOverviewPropertiesDto
            {
                Total = properties.Total,
                Short = properties.Short,
                Long = properties.Long,
                ToComplete = properties.ToComplete,
            }
            : null,
        Invitations = overview.Invitations is { } invitations
            ? new OrgOverviewInvitationsDto { Pending = invitations.Pending, OldestPendingDays = invitations.OldestPendingDays }
            : null,
        AccessRequests = overview.AccessRequests is { } requests ? new OrgOverviewAccessRequestsDto { Pending = requests.Pending } : null,
        Suppliers = overview.Suppliers is { } suppliers ? new OrgOverviewSuppliersDto { Trusted = suppliers.Trusted } : null,
        Alloggiati = overview.Alloggiati is { } alloggiati
            ? new OrgOverviewAlloggiatiDto { PropertiesWithoutCredentials = alloggiati.PropertiesWithoutCredentials }
            : null,
        RecentActivity = overview.RecentActivity?.Select(OrgActivityEntryDto.From).ToList(),
    };
}

/// <summary>Seats in use out of what the plan allows. <c>Max</c> is 2147483647 (int max) when unlimited, like <c>GET /api/orgs/me/members</c>.</summary>
public class OrgOverviewSeatsDto
{
    public int Used { get; set; }
    public int Max { get; set; }
    public bool Unlimited { get; set; }
}

/// <summary>The properties the caller reaches: <c>Total</c> = <c>Short</c> + <c>Long</c>; <c>ToComplete</c> are short-term ones that cannot receive bookings yet.</summary>
public class OrgOverviewPropertiesDto
{
    public int Total { get; set; }
    public int Short { get; set; }
    public int Long { get; set; }
    public int ToComplete { get; set; }
}

/// <summary>Invitations that can still be accepted; <c>OldestPendingDays</c> are the whole days the oldest has waited (null with none).</summary>
public class OrgOverviewInvitationsDto
{
    public int Pending { get; set; }
    public int? OldestPendingDays { get; set; }
}

public class OrgOverviewAccessRequestsDto
{
    public int Pending { get; set; }
}

public class OrgOverviewSuppliersDto
{
    public int Trusted { get; set; }
}

/// <summary>Short-term properties of the caller with no Alloggiati Web credentials.</summary>
public class OrgOverviewAlloggiatiDto
{
    public int PropertiesWithoutCredentials { get; set; }
}
