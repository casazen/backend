using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The part «Guest registration» (E29/F02/US01/T02, NF-129): the notification of the guests of the Swiss properties. It is done only when every Swiss
/// property the plan counts has its municipality and, in Ticino, the structure number the police file starts with; it names, property by property, what is
/// missing, with the reason and the step of the wizard the frontend reopens (<see cref="PartFacts.Missing"/>).
/// </summary>
/// <remarks>
/// <para><b>Counts the Swiss properties.</b> <see cref="PartFacts.Done"/> over <see cref="PartFacts.Total"/> are the Swiss properties among the ones the
/// plan counts (<see cref="OnboardingPartContext.PropertyIds"/>: short-rent, active, in the reach of the caller) and the ones with nothing missing. An
/// organization with no such property has nothing to do here: the part is not available (<see cref="OnboardingPartReasons.NoSwissProperty"/>), and no Italian
/// property is ever counted. The rule is the one of the activation of a property and of <c>GET api/properties/{id}/guest-registration</c>
/// (<see cref="GuestRegistrationReadiness"/>); a pending property is not counted by the plan, the activation of the property says what it lacks.</para>
/// <para><b>Reach.</b> Only the properties of <see cref="OnboardingPartContext.PropertyIds"/> are read, every query names the org, and an empty list is
/// an empty answer, never «every property of the org» (a collaborator «Solo alcuni» sees only its own).</para>
/// <para><b>No flag, no setting of the host.</b> The part is not behind <c>Features:GuestRegistrationRegimes</c> and never reads the host's setting. Nothing
/// is written; only codes, counts and ids are returned.</para>
/// </remarks>
public sealed class GuestRegistrationPartEvaluator(AppDbContext db, IPropertySwissPlaceResolver places) : IOnboardingPartEvaluator
{
    /// <summary>The step of the activation wizard of a property that the items point to (the same id as <see cref="PropertyComplianceStatusService.GuestRegistrationStepId"/>).</summary>
    public const string StepCode = PropertyComplianceStatusService.GuestRegistrationStepId;

    public OnboardingPartKey Part => OnboardingPartKey.GuestRegistration;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = context.PropertyIds.ToList();
        if (ids.Count == 0)
            return PartFacts.NotAvailable(OnboardingPartReasons.NoSwissProperty);

        var swiss = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == context.OrgId && ids.Contains(p.Id) && p.Country.Trim().ToUpper() == "CH")
            .Select(p => new SwissFacts(p.Id, p.Country, p.SwissMunicipalityCode, p.PostalCode, p.City, p.TicinoStructureId))
            .ToListAsync(ct);
        if (swiss.Count == 0)
            return PartFacts.NotAvailable(OnboardingPartReasons.NoSwissProperty);

        var placeOf = await places.ResolveManyAsync(
            [.. swiss.Select(p => new SwissPlaceLookup(p.Id, p.Country, p.SwissMunicipalityCode, p.PostalCode, p.City))], ct);

        // In the order the plan gives the properties (a stable one), the first reason of each property that lacks something.
        var byId = swiss.ToDictionary(p => p.Id);
        var missing = new List<PartMissingItem>();
        foreach (var id in ids.Where(byId.ContainsKey))
        {
            var property = byId[id];
            var reasons = GuestRegistrationReadiness.MissingOf(property.Country, placeOf.GetValueOrDefault(id), property.TicinoStructureId);
            if (reasons.Count > 0)
                missing.Add(new PartMissingItem(id, reasons[0], StepCode));
        }

        // The reason of the part is the first thing to do: a municipality before a structure number (nobody knows a property is in Ticino without it).
        var reason = missing.Any(item => item.Reason == OnboardingPartReasons.SwissMunicipalityMissing)
            ? OnboardingPartReasons.SwissMunicipalityMissing
            : OnboardingPartReasons.TicinoStructureIdMissing;
        var facts = PartFacts.Counted(swiss.Count - missing.Count, swiss.Count, reason);
        return facts with { Missing = [.. missing.Take(PartFacts.MaxMissingItems)] };
    }

    private sealed record SwissFacts(Guid Id, string Country, string? SwissMunicipalityCode, string? PostalCode, string? City, string? TicinoStructureId);
}
