namespace Casazen.Core.Suppliers;

/// <summary>
/// What a supplier sends to <c>PUT api/supplier/availability/urgent</c> (SP-04c, nothing checked yet): <b>every</b> field is needed
/// (<c>PUT</c> replaces them; a field that is left out is a 422 naming it, never reset to its default).
/// </summary>
/// <param name="Enabled">The supplier accepts urgent requests (for the services that offer them too).</param>
/// <param name="FromMinute">Where the urgent window starts, in minutes after midnight on the clock of Rome (0 to 1439).</param>
/// <param name="ToMinute">Where it ends (1 to 1440, after <paramref name="FromMinute"/>).</param>
/// <param name="SurchargeCents">The supplier's default surcharge, in euro cents (0 to <see cref="SupplierAgendaLimits.MaxUrgentSurchargeCents"/>).</param>
public sealed record SupplierUrgentInput(bool? Enabled, int? FromMinute, int? ToMinute, int? SurchargeCents);

/// <summary>
/// The supplier's own urgent settings (SP-04c, decision SP-D9): whether it accepts urgent requests at all, the window of the day in
/// which it does (<b>outside its normal hours too</b>), and the surcharge a service that has none of its own asks. A supplier that
/// never saved them has <see cref="Default"/> (off, 07:00 to 22:00, no surcharge). The slot planner reads it from its input
/// (<see cref="SupplierPlanningInput.Urgent"/>).
/// </summary>
public sealed record SupplierUrgentSettings(bool Enabled, int FromMinute, int ToMinute, int SurchargeCents)
{
    public static SupplierUrgentSettings Default { get; } = new(
        false,
        SupplierAgendaDefaults.UrgentFromMinute,
        SupplierAgendaDefaults.UrgentToMinute,
        SupplierAgendaDefaults.UrgentSurchargeCents);

    public bool HasWindow => FromMinute >= 0 && ToMinute <= SupplierAgendaLimits.MinutesPerDay && FromMinute < ToMinute;

    public bool IsOn => Enabled && HasWindow;
}

public sealed record SupplierUrgentTerms(bool Enabled, int SurchargeCents)
{
    public static SupplierUrgentTerms Off { get; } = new(false, 0);
}

public static class SupplierUrgentFields
{
    public const string Enabled = "enabled";
    public const string FromMinute = "fromMinute";
    public const string ToMinute = "toMinute";
    public const string SurchargeCents = "surchargeCents";
}

public static class SupplierUrgentRules
{
    public static SupplierUrgentSettings Normalize(SupplierUrgentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var invalid = new List<string>();
        var enabled = false;
        if (input.Enabled is { } flag)
            enabled = flag;
        else
            invalid.Add(SupplierUrgentFields.Enabled);

        var from = 0;
        var to = 0;
        if (input.FromMinute is >= 0 and < SupplierAgendaLimits.MinutesPerDay)
            from = input.FromMinute.Value;
        else
            invalid.Add(SupplierUrgentFields.FromMinute);

        if (input.ToMinute is > 0 and <= SupplierAgendaLimits.MinutesPerDay)
        {
            to = input.ToMinute.Value;
            if (!invalid.Contains(SupplierUrgentFields.FromMinute) && to <= from)
                invalid.Add(SupplierUrgentFields.ToMinute);
        }
        else
        {
            invalid.Add(SupplierUrgentFields.ToMinute);
        }

        var surcharge = 0;
        if (input.SurchargeCents is { } cents && IsValidSurcharge(cents))
            surcharge = cents;
        else
            invalid.Add(SupplierUrgentFields.SurchargeCents);

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidUrgent(invalid);

        return new SupplierUrgentSettings(enabled, from, to, surcharge);
    }

    public static bool IsValidSurcharge(int cents) => cents is >= 0 and <= SupplierAgendaLimits.MaxUrgentSurchargeCents;

    public static SupplierUrgentTerms Effective(bool serviceEnabled, int? serviceSurchargeCents, SupplierUrgentSettings supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        return serviceEnabled && supplier.IsOn
            ? new SupplierUrgentTerms(true, serviceSurchargeCents ?? supplier.SurchargeCents)
            : SupplierUrgentTerms.Off;
    }
}
