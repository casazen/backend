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
    /// <summary>422: the weekly hours are not valid (overlapping bands, an end that is not after its start, more than 3 bands a day, a repeated weekday).</summary>
    public const string HoursInvalid = "supplier_hours_invalid";

    /// <summary>422: the time off is not valid (dates missing or reversed, too long, already over, too far ahead, an unknown reason, a label that is too long).</summary>
    public const string TimeOffInvalid = "supplier_time_off_invalid";

    /// <summary>422: the supplier already has <see cref="SupplierAgendaLimits.MaxTimeOffEntries"/> time off entries that have not ended.</summary>
    public const string TimeOffLimitReached = "supplier_time_off_limit_reached";

    /// <summary>404: no time off with that id for the caller (another supplier's, or already deleted, answers the same).</summary>
    public const string TimeOffNotFound = "supplier_time_off_not_found";

    /// <summary>422: the block or extra opening is not valid (end not after start, too short or too long, already over, too far ahead, a kind the console cannot create, an extra opening that is not in one day).</summary>
    public const string BlockInvalid = "supplier_block_invalid";

    /// <summary>422: the supplier already has <see cref="SupplierAgendaLimits.MaxManualWindows"/> blocks and extra openings that have not ended.</summary>
    public const string BlockLimitReached = "supplier_block_limit_reached";

    /// <summary>404: no block or extra opening with that id for the caller (another supplier's, one of the supplier's calendar feed, or deleted, answers the same).</summary>
    public const string BlockNotFound = "supplier_block_not_found";

    /// <summary>422: a rule is missing or outside its limits (buffer, jobs a day, notice, horizon, slot step).</summary>
    public const string RulesInvalid = "supplier_rules_invalid";

    /// <summary>
    /// Every <c>SharedResources</c> key the agenda uses (a test checks that each one exists in Italian and English). The
    /// range of <c>GET api/supplier/calendar</c> reuses <c>SupplierAvailabilityRangeInvalid</c> and
    /// <c>SupplierAvailabilityRangeTooLong</c> of the availability endpoint.
    /// </summary>
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
    ];

    /// <summary>The 422 of weekly hours that are not valid; <paramref name="fields"/> are the JSON names of what is wrong.</summary>
    public static SupplierAgendaRuleException InvalidHours(IReadOnlyList<string> fields) =>
        new(HoursInvalid, "SupplierHoursInvalid", fields);

    /// <summary>The 422 of a time off that is not valid.</summary>
    public static SupplierAgendaRuleException InvalidTimeOff(IReadOnlyList<string> fields) =>
        new(TimeOffInvalid, "SupplierTimeOffInvalid", fields);

    /// <summary>The 422 of a block or extra opening that is not valid.</summary>
    public static SupplierAgendaRuleException InvalidBlock(IReadOnlyList<string> fields) =>
        new(BlockInvalid, "SupplierBlockInvalid", fields);

    /// <summary>The 422 of rules that are not valid.</summary>
    public static SupplierAgendaRuleException InvalidRules(IReadOnlyList<string> fields) =>
        new(RulesInvalid, "SupplierRulesInvalid", fields);

    /// <summary>The 422 of too many time off entries.</summary>
    public static DomainRuleException TimeOffLimit() =>
        new(TimeOffLimitReached, "SupplierTimeOffLimitReached", SupplierAgendaLimits.MaxTimeOffEntries);

    /// <summary>The 422 of too many blocks and extra openings.</summary>
    public static DomainRuleException BlockLimit() =>
        new(BlockLimitReached, "SupplierBlockLimitReached", SupplierAgendaLimits.MaxManualWindows);

    /// <summary>The 404 of a time off that is not the caller's.</summary>
    public static NotFoundException TimeOffMissing(Guid id) =>
        new($"Supplier time off {id} not found")
        {
            Code = TimeOffNotFound,
            MessageKey = "SupplierTimeOffNotFound",
        };

    /// <summary>The 404 of a block or extra opening that is not the caller's, or that the caller cannot delete.</summary>
    public static NotFoundException BlockMissing(Guid id) =>
        new($"Supplier block {id} not found")
        {
            Code = BlockNotFound,
            MessageKey = "SupplierBlockNotFound",
        };
}

/// <summary>
/// A 422 of the agenda rules that names the fields at fault: the controller adds them to the response as <c>fields</c>, so
/// the form can mark them. Anything that does not catch it gets the plain 422 with code and message from the error
/// middleware.
/// </summary>
public sealed class SupplierAgendaRuleException : DomainRuleException
{
    public SupplierAgendaRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    /// <summary>JSON names of the fields at fault (<c>days[1].bands[0].endMinute</c>, <c>fromDate</c>, <c>startUtc</c>, <c>bufferMinutes</c>...).</summary>
    public IReadOnlyList<string> Fields { get; }
}
