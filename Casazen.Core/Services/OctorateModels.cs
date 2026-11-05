using System.Text.Json.Serialization;

namespace Casazen.Core.Services;

/// <summary>
/// The application's configuration as Octorate reports it (<c>GET /rest/v1/api/configuration</c>).
/// </summary>
/// <param name="Environment">Which environment the credentials belong to: <c>SANDBOX</c> or <c>PRODUCTION</c>.</param>
/// <param name="QuotaLimit">The quota of calls of the application (it depends on the category Octorate assigned).</param>
/// <param name="QuotaStatus">What is left of the quota as Octorate counts it (not a simple countdown, seen in the sandbox).</param>
/// <param name="Category">The application category Octorate assigned (read-only for CasaZen), empty when none.</param>
public sealed record OctorateConfiguration(string? Environment, int? QuotaLimit, int? QuotaStatus, string? Category);

/// <summary>
/// An accommodation as Octorate describes it (<c>GET /rest/v1/accommodation</c>, <c>GET /rest/v1/accommodation/{acc}</c>): only the
/// members CasaZen uses. The contact person, e-mail, phone and tax codes Octorate also returns are not read.
/// </summary>
public sealed record OctorateProperty
{
    /// <summary>The accommodation id (<c>id</c>), the key of every other call about it.</summary>
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>Street and number (<c>address</c>, else <c>location.address</c>).</summary>
    public string? Address { get; init; }

    public string? City { get; init; }

    /// <summary>The postal code (<c>zipCode</c>).</summary>
    public string? PostalCode { get; init; }

    /// <summary>The country code (<c>country</c>, ISO alpha-2).</summary>
    public string? Country { get; init; }

    /// <summary>The coordinates; <c>null</c> when Octorate has none (a 0,0 pair is read as none).</summary>
    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    /// <summary>The kind of accommodation (<c>propertyCategory</c>: <c>APARTMENT</c>, <c>HOUSE</c>, ...).</summary>
    public string? Category { get; init; }

    /// <summary>The network the accommodation is reached through (<c>networkInfo.accessNetwork</c>), when it is in one.</summary>
    public string? NetworkId { get; init; }

    /// <summary>The <c>sandboxProperty</c> flag of a test accommodation, when Octorate sends it.</summary>
    public bool? SandboxProperty { get; init; }
}

/// <summary>
/// A partial change of an accommodation (<c>PATCH /rest/v2/accommodation/{acc}</c>): only the members that are set are sent, the
/// others stay as they are on Octorate.
/// </summary>
public sealed record OctoratePropertyUpdate
{
    public string? Name { get; init; }

    public string? Address { get; init; }

    public string? City { get; init; }

    public string? ZipCode { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    /// <summary>The first hour of check-in (0-23).</summary>
    public int? CheckinStart { get; init; }

    /// <summary>The last hour of check-in (0-23).</summary>
    public int? CheckinEnd { get; init; }

    /// <summary>The hour of check-out (0-23).</summary>
    public int? Checkout { get; init; }

    /// <summary>True when no member is set: there is nothing to send.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Name is null && Address is null && City is null && ZipCode is null && Latitude is null && Longitude is null
        && CheckinStart is null && CheckinEnd is null && Checkout is null;
}
