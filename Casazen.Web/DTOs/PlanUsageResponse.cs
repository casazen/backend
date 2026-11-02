namespace Casazen.Web.DTOs;

/// <summary>
/// The org's use of the property limit of its plan (<c>GET /api/properties/plan-usage</c>, SR-07a): the line «Piano: 11 di 50
/// immobili» of the host's list and the check before «Aggiungi immobile». Four numbers and flags, deliberately nothing else: not
/// the name of the plan, not the subscription, not the billing, which only the billing administrator reads.
/// </summary>
/// <param name="Used">Properties of the org the plan limit counts: short and long, paused included, deleted not.</param>
/// <param name="Max">The limit; <c>null</c> when the plan has none (<paramref name="Unlimited"/>).</param>
/// <param name="Unlimited">The plan has no limit on properties.</param>
/// <param name="CanAdd">Another property can be created without reaching the limit.</param>
public sealed record PlanUsageResponse(int Used, int? Max, bool Unlimited, bool CanAdd);
