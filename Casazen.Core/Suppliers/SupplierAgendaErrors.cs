using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the
/// errors of the supplier's agenda (SP-03, <c>api/supplier/availability/*</c>). 404 for a time off or a window that is not
/// the caller's (or not there), 422 for every other one. The codes are snake_case and never renamed: the frontend branches
/// on them. A 422 about a value names the fields at fault (<see cref="SupplierAgendaRuleException.Fields"/>).
/// </summary>
public static class SupplierAgendaErrors
{
    public const string HoursInvalid = "supplier_hours_invalid";

    public const string TimeOffInvalid = "supplier_time_off_invalid";

    public const string TimeOffLimitReached = "supplier_time_off_limit_reached";

    public const string TimeOffNotFound = "supplier_time_off_not_found";

    public const string BlockInvalid = "supplier_block_invalid";

    public const string BlockLimitReached = "supplier_block_limit_reached";

    public const string BlockNotFound = "supplier_block_not_found";

    public const string RulesInvalid = "supplier_rules_invalid";

    public const string SettingsInvalid = "supplier_settings_invalid";

    public const string UrgentInvalid = "supplier_urgent_invalid";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "SupplierHoursInvalid",
        "SupplierTimeOffInvalid",
        "SupplierTimeOffLimitReached",
        "SupplierTimeOffNotFound",
        "SupplierBlockInvalid",
        "SupplierBlockLimitReached",
        "SupplierBlockNotFound",
        "SupplierRulesInvalid",
        "SupplierSettingsInvalid",
        "SupplierUrgentInvalid",
    ];

    public static SupplierAgendaRuleException InvalidHours(IReadOnlyList<string> fields) =>
        new(HoursInvalid, "SupplierHoursInvalid", fields);

    public static SupplierAgendaRuleException InvalidTimeOff(IReadOnlyList<string> fields) =>
        new(TimeOffInvalid, "SupplierTimeOffInvalid", fields);

    public static SupplierAgendaRuleException InvalidBlock(IReadOnlyList<string> fields) =>
        new(BlockInvalid, "SupplierBlockInvalid", fields);

    public static SupplierAgendaRuleException InvalidRules(IReadOnlyList<string> fields) =>
        new(RulesInvalid, "SupplierRulesInvalid", fields);

    public static SupplierAgendaRuleException InvalidSettings(IReadOnlyList<string> fields) =>
        new(SettingsInvalid, "SupplierSettingsInvalid", fields);

    public static SupplierAgendaRuleException InvalidUrgent(IReadOnlyList<string> fields) =>
        new(UrgentInvalid, "SupplierUrgentInvalid", fields);

    public static DomainRuleException TimeOffLimit() =>
        new(TimeOffLimitReached, "SupplierTimeOffLimitReached", SupplierAgendaLimits.MaxTimeOffEntries);

    public static DomainRuleException BlockLimit() =>
        new(BlockLimitReached, "SupplierBlockLimitReached", SupplierAgendaLimits.MaxManualWindows);

    public static NotFoundException TimeOffMissing(Guid id) =>
        new($"Supplier time off {id} not found")
        {
            Code = TimeOffNotFound,
            MessageKey = "SupplierTimeOffNotFound",
        };

    public static NotFoundException BlockMissing(Guid id) =>
        new($"Supplier block {id} not found")
        {
            Code = BlockNotFound,
            MessageKey = "SupplierBlockNotFound",
        };
}

public sealed class SupplierAgendaRuleException : DomainRuleException
{
    public SupplierAgendaRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    public IReadOnlyList<string> Fields { get; }
}
