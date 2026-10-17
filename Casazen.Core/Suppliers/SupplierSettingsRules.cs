namespace Casazen.Core.Suppliers;

/// <summary>
/// What a supplier sends to <c>PUT api/supplier/settings</c> (SP-16a, nothing checked yet): <b>every</b> field is needed (<c>PUT</c>
/// replaces them; a field that is left out is a 422 naming it, never reset to its default). <c>onlineBookingEnabled</c> is not here:
/// it is read-only in the console and only the showcase editor writes it (decision SP-D29).
/// </summary>
public sealed record SupplierSettingsInput(
    bool? AutoAcceptRegulars,
    bool? NotifyNewRequests,
    bool? NotifyDayBeforeReminder,
    bool? NotifyPayments,
    int? RespondWithinMinutes);

/// <summary>The settings after <see cref="SupplierSettingsRules.Normalize"/>: all present and inside their limits.</summary>
public sealed record SupplierSettingsContent(
    bool AutoAcceptRegulars,
    bool NotifyNewRequests,
    bool NotifyDayBeforeReminder,
    bool NotifyPayments,
    int RespondWithinMinutes);

/// <summary>
/// JSON names of the fields of <c>api/supplier/settings</c>, as they appear in <c>fields</c> of a 422
/// (<see cref="SupplierAgendaRuleException.Fields"/>).
/// </summary>
public static class SupplierSettingsFields
{
    public const string AutoAcceptRegulars = "autoAcceptRegulars";
    public const string NotifyNewRequests = "notifyNewRequests";
    public const string NotifyDayBeforeReminder = "notifyDayBeforeReminder";
    public const string NotifyPayments = "notifyPayments";
    public const string RespondWithinMinutes = "respondWithinMinutes";
}

/// <summary>
/// What is a valid value of the supplier's own settings (SP-16a): the three notification switches and the automatic acceptance of the
/// regular customers are booleans, and the time to answer a request of the public showcase is a whole number of minutes from
/// <see cref="SupplierAgendaLimits.MinRespondWithinMinutes"/> to <see cref="SupplierAgendaLimits.MaxRespondWithinMinutes"/> (the same
/// bounds as the database check). Pure, testable without a database; every value that is not valid is collected and refused together
/// (422 <see cref="SupplierAgendaErrors.SettingsInvalid"/> naming all the fields at fault).
/// </summary>
public static class SupplierSettingsRules
{
    public static SupplierSettingsContent Normalize(SupplierSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var invalid = new List<string>();
        var autoAccept = Required(input.AutoAcceptRegulars, SupplierSettingsFields.AutoAcceptRegulars, invalid);
        var newRequests = Required(input.NotifyNewRequests, SupplierSettingsFields.NotifyNewRequests, invalid);
        var reminder = Required(input.NotifyDayBeforeReminder, SupplierSettingsFields.NotifyDayBeforeReminder, invalid);
        var payments = Required(input.NotifyPayments, SupplierSettingsFields.NotifyPayments, invalid);

        var minutes = 0;
        if (input.RespondWithinMinutes is { } value
            && value >= SupplierAgendaLimits.MinRespondWithinMinutes
            && value <= SupplierAgendaLimits.MaxRespondWithinMinutes)
            minutes = value;
        else
            invalid.Add(SupplierSettingsFields.RespondWithinMinutes);

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidSettings(invalid);

        return new SupplierSettingsContent(autoAccept, newRequests, reminder, payments, minutes);
    }

    private static bool Required(bool? value, string field, List<string> invalid)
    {
        if (value is { } flag)
            return flag;

        invalid.Add(field);
        return false;
    }
}
