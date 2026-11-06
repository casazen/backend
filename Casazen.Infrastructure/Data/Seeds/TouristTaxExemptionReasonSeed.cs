using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// The reasons of a tourist tax exemption (NF-46, ADR-037-R1), applied by the migration <c>AddGuestTouristTax</c> through <c>HasData</c>:
/// <see cref="TouristTaxExemptionReason.MinorAgeCode"/> (automatic, the calculator applies it from the age) and the host-chosen categories the
/// regulation context lists as typically exempt (<c>.claude/context/regulations/imposta_soggiorno.md</c>, "Esenzioni Comuni": residents, health
/// or care stays, companions of disabled people, law enforcement on duty), plus the manager's family and "other". The ids are fixed so that the
/// migration never changes; every Octorate code is null (the Octorate reasons are not in the repository, Q-004).
/// </summary>
public static class TouristTaxExemptionReasonSeed
{
    /// <summary>The date the rows were written (fixed: a migration must not change after it ran).</summary>
    public static readonly DateTime SeededAt = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>New instances of the seeded reasons on every call (entities are mutable).</summary>
    public static IReadOnlyList<TouristTaxExemptionReason> Build() =>
    [
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9001", TouristTaxExemptionReason.MinorAgeCode, isAutomatic: true, sortOrder: 0),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9002", TouristTaxExemptionReason.ResidentCode, isAutomatic: false, sortOrder: 10),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9003", TouristTaxExemptionReason.HealthCareCode, isAutomatic: false, sortOrder: 20),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9004", TouristTaxExemptionReason.DisabilityCompanionCode, isAutomatic: false, sortOrder: 30),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9005", TouristTaxExemptionReason.PublicServiceCode, isAutomatic: false, sortOrder: 40),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9006", TouristTaxExemptionReason.HostFamilyCode, isAutomatic: false, sortOrder: 50),
        Reason("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9007", TouristTaxExemptionReason.OtherCode, isAutomatic: false, sortOrder: 60),
    ];

    /// <summary>The i18n key of the label of a reason code.</summary>
    public static string LabelKeyOf(string code) => "touristTax.exemption." + code;

    private static TouristTaxExemptionReason Reason(string id, string code, bool isAutomatic, int sortOrder) => new()
    {
        Id = Guid.Parse(id),
        Code = code,
        LabelKey = LabelKeyOf(code),
        IsAutomatic = isAutomatic,
        OctorateExemptionCode = null,
        SortOrder = sortOrder,
        IsActive = true,
        UpdatedAt = SeededAt,
        UpdatedByUserId = null,
    };
}
