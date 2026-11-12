using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>GET api/marketplace/functions?propertyId=</c> and <c>POST api/marketplace/functions/refresh</c> (E09/F02/US02/T01, NF-56): the functions of the
/// channel manager's license for one property. <c>tier</c> and <c>roomsLimit</c> are those of the last live read and <c>licenseReadAt</c> says when (all
/// <c>null</c> until a read, also after a restart); <c>noRoomLicense</c> is true when the limit read is 0: without a license per room no prices and
/// no availability are published, and the page says so. <c>functions</c> are the twelve modules in the order of the catalog.
/// </summary>
public sealed record MarketplaceFunctionListDto(
    Guid PropertyId,
    string? Tier,
    int? RoomsLimit,
    bool NoRoomLicense,
    DateTime? LicenseReadAt,
    IReadOnlyList<MarketplaceFunctionDto> Functions)
{
    public static MarketplaceFunctionListDto From(MarketplaceFunctionList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new MarketplaceFunctionListDto(
            list.PropertyId,
            list.Tier,
            list.RoomsLimit,
            list.NoRoomLicense,
            list.LicenseReadAt,
            [.. list.Functions.Select(MarketplaceFunctionDto.From)]);
    }
}

/// <summary>
/// One function of the license for the property, and the answer of <c>POST api/marketplace/functions/{moduleCode}/request</c>. <c>status</c> is
/// <c>NotActive</c>, <c>Requested</c> (the host asked and the license does not show it yet; <c>isDeclared</c> is true), <c>Active</c> (the license shows
/// it) or <c>ToFix</c> (it was on and the license no longer shows it: <c>lastErrorCode</c> is <c>license_module_off</c>). <c>requestGuidance</c> is what to
/// ask the channel manager, in the language asked, with the name of the function and of the property put in; <c>null</c> for an Active function.
/// <c>changeRequestedAt</c> is set while a change the host asked for is waiting for the license.
/// </summary>
public sealed record MarketplaceFunctionDto(
    string Code,
    string Name,
    MarketplaceFunctionStatus Status,
    Guid? ActivationId,
    bool IsDeclared,
    DateTime? RequestedAt,
    DateTime? VerifiedAt,
    DateTime? LastSyncedAt,
    string? LastErrorCode,
    DateTime? ChangeRequestedAt,
    string? RequestGuidance)
{
    public static MarketplaceFunctionDto From(MarketplaceFunctionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MarketplaceFunctionDto(
            entry.Code,
            entry.Name,
            entry.Status,
            entry.ActivationId,
            entry.IsDeclared,
            entry.RequestedAt,
            entry.VerifiedAt,
            entry.LastSyncedAt,
            entry.LastErrorCode,
            entry.ChangeRequestedAt,
            entry.RequestGuidance);
    }
}

/// <summary>The body of <c>POST api/marketplace/functions/refresh</c> and of <c>POST api/marketplace/functions/{moduleCode}/request</c>: the property.</summary>
public sealed class MarketplaceFunctionPropertyRequest
{
    /// <summary>The property the license is read for, or the function is asked for. A license belongs to a property.</summary>
    [Required(ErrorMessage = "OctorateMappingPropertyRequired")]
    public Guid? PropertyId { get; set; }
}
