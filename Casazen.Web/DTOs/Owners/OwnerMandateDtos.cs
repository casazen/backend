using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Owners;

/// <summary>
/// A mandate version of an owner (E13/F01/US01/T01): <c>GET/POST api/owners/{ownerId}/mandates</c>. <see cref="OwnershipId"/> is set only on a mandate of one
/// ownership of before E13; the versions the API writes cover every property of the owner. The signed document is only said to be on file
/// (<see cref="HasDocument"/>), never returned.
/// </summary>
public sealed record OwnerMandateDto(
    Guid Id,
    Guid OwnerId,
    Guid? OwnershipId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal CommissionPercent,
    CommissionBase CommissionBase,
    bool CommissionIncludesVat,
    decimal? CleaningFlatRate,
    decimal? LinenFlatRate,
    bool PassthroughPaymentFees,
    CommissionTreatment WithholdingTreatment,
    int MonthlyFrequency,
    bool HasDocument,
    DateTime CreatedAt,
    string? CreatedByUserId)
{
    public static OwnerMandateDto From(OwnerMandate m) => new(
        m.Id,
        m.PropertyOwnerId,
        m.PropertyOwnershipId,
        m.ValidFrom,
        m.ValidTo,
        m.CommissionPercent,
        m.CommissionBase,
        m.CommissionIncludesVat,
        m.CleaningFlatRate,
        m.LinenFlatRate,
        m.PassthroughPaymentFees,
        m.WithholdingTreatment,
        m.MonthlyFrequency,
        !string.IsNullOrWhiteSpace(m.MandatePdfUrl),
        m.CreatedAt,
        m.CreatedByUserId);
}

/// <summary>
/// The structured billing address of an owner (<c>GET/PUT api/owners/{id}/address</c>), in clear: only callers with the finance permission get it, and every
/// read is logged with ids, never values.
/// </summary>
public sealed record OwnerAddressDto(
    Guid OwnerId,
    string? Street,
    string? BuildingNumber,
    string? PostalCode,
    string? Town,
    string? Province,
    string? Country)
{
    public static OwnerAddressDto From(PropertyOwner owner) => new(
        owner.Id, owner.BillingStreet, owner.BillingCivic, owner.BillingPostalCode, owner.BillingCity, owner.BillingProvince, owner.BillingCountry);
}

/// <summary>A commission rule of a channel of the agency, as stored.</summary>
public sealed record ChannelCommissionRuleDto(
    Guid Id,
    string Channel,
    decimal Percent,
    CommissionBase Base,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    DateTime CreatedAt)
{
    public static ChannelCommissionRuleDto From(OrgChannelCommissionRule r) => new(r.Id, r.Channel, r.Percent, r.Base, r.ValidFrom, r.ValidTo, r.CreatedAt);
}

/// <summary>
/// The channel commission rules of the agency (<c>GET/PUT api/owner-accounting/channel-commission-rules</c>): <see cref="Rules"/> is the list a <c>PUT</c>
/// replaces; <see cref="Channels"/> says, for each portal, what applies today: the rule in force, or the configured default (<c>isDefault: true</c>), or
/// nothing (<c>percent: null</c>).
/// </summary>
public sealed record ChannelCommissionRuleSetDto(IReadOnlyList<ChannelCommissionRuleDto> Rules, IReadOnlyList<ChannelCommissionInForce> Channels)
{
    public static ChannelCommissionRuleSetDto From(ChannelCommissionRuleSet set) =>
        new(set.Rules.Select(ChannelCommissionRuleDto.From).ToList(), set.Channels);
}
