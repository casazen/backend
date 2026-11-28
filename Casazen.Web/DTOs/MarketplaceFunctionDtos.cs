using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

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

public sealed class MarketplaceFunctionPropertyRequest
{
    [Required(ErrorMessage = "OctorateMappingPropertyRequired")]
    public Guid? PropertyId { get; set; }
}
