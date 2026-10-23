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
    /// <summary>What a supplier that never saved the settings has: off, 07:00 to 22:00, no surcharge.</summary>
    public static SupplierUrgentSettings Default { get; } = new(
        false,
        SupplierAgendaDefaults.UrgentFromMinute,
        SupplierAgendaDefaults.UrgentToMinute,
        SupplierAgendaDefaults.UrgentSurchargeCents);

    /// <summary>True when the window has a length (the database check keeps it so; a hand-made value is not trusted).</summary>
    public bool HasWindow => FromMinute >= 0 && ToMinute <= SupplierAgendaLimits.MinutesPerDay && FromMinute < ToMinute;

    /// <summary>True when the supplier accepts urgent requests and has a window to accept them in: the only case with urgent slots.</summary>
    public bool IsOn => Enabled && HasWindow;
}

/// <summary>
/// What a service offers for an urgent request once the supplier's settings are taken into account (SP-04c): <see cref="Enabled"/>
/// only when the supplier <b>and</b> the service both accept it, and <see cref="SurchargeCents"/> the amount added to the estimate
/// (the service's own, else the supplier's default). The only shape in which the public learns of it.
/// </summary>
public sealed record SupplierUrgentTerms(bool Enabled, int SurchargeCents)
{
    /// <summary>No urgent requests for this service: no surcharge to show.</summary>
    public static SupplierUrgentTerms Off { get; } = new(false, 0);
}

/// <summary>
/// JSON names of the fields of <c>api/supplier/availability/urgent</c>, as they appear in <c>fields</c> of a 422
/// (<see cref="SupplierAgendaRuleException.Fields"/>).
/// </summary>
public static class SupplierUrgentFields
{
    public const string Enabled = "enabled";
    public const string FromMinute = "fromMinute";
    public const string ToMinute = "toMinute";
    public const string SurchargeCents = "surchargeCents";
}

/// <summary>
/// The rules of the urgent requests (SP-04c, decision SP-D9). Pure functions, testable without a database. Today and tomorrow only
/// (Europe/Rome), 30 minutes of notice, the window the supplier sets (07:00 to 22:00 by default), a fixed surcharge in euro that is
/// the service's own or else the supplier's default; the holidays, the days closed by hand, the blocks, the other requests, the
/// pause, the daily maximum, the weekdays of the service and the step of the slots all stay as they are for a normal request.
/// </summary>
public static class SupplierUrgentRules
{
    /// <summary>
    /// Checks the settings a supplier sends. Refused (422 <see cref="SupplierAgendaErrors.UrgentInvalid"/>, every field at fault named
    /// together): a field that is missing, a start outside 0-1439, an end outside 1-1440 or not after the start, a surcharge outside
    /// 0 to <see cref="SupplierAgendaLimits.MaxUrgentSurchargeCents"/> (the same bounds as the database checks).
    /// </summary>
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

    /// <summary>True for a surcharge a supplier or a service may ask: from 0 to <see cref="SupplierAgendaLimits.MaxUrgentSurchargeCents"/> cents.</summary>
    public static bool IsValidSurcharge(int cents) => cents is >= 0 and <= SupplierAgendaLimits.MaxUrgentSurchargeCents;

    /// <summary>
    /// The urgent terms of one service for the public: on only when the service accepts urgent requests <b>and</b> the supplier does
    /// (the service keeps its own switch, but the supplier's off wins: the switch stays, the effect does not), with a window. The
    /// surcharge is the service's own when it has one (<c>0</c> is a surcharge: an urgency for free), else the supplier's default.
    /// </summary>
    public static SupplierUrgentTerms Effective(bool serviceEnabled, int? serviceSurchargeCents, SupplierUrgentSettings supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        return serviceEnabled && supplier.IsOn
            ? new SupplierUrgentTerms(true, serviceSurchargeCents ?? supplier.SurchargeCents)
            : SupplierUrgentTerms.Off;
    }
}
