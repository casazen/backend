using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

public class OrgOverviewResponse
{
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewAccessRequestsDto? AccessRequests { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewSuppliersDto? Suppliers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OrgOverviewAlloggiatiDto? Alloggiati { get; set; }

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

public class OrgOverviewSeatsDto
{
    public int Used { get; set; }
    public int Max { get; set; }
    public bool Unlimited { get; set; }
}

public class OrgOverviewPropertiesDto
{
    public int Total { get; set; }
    public int Short { get; set; }
    public int Long { get; set; }
    public int ToComplete { get; set; }
}

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

public class OrgOverviewAlloggiatiDto
{
    public int PropertiesWithoutCredentials { get; set; }
}
