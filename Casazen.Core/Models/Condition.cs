namespace Casazen.Core.Models;

/// <summary>
/// Condition tree for marketing segments and automation routines (NF-07, ADR-014).
/// Server-side evaluation only - client never receives full guest data for evaluation.
/// </summary>
public class Condition
{
    /// <summary>Schema version for migration (ADR-014-R11).</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>Top-level condition groups combined with AND/OR logic.</summary>
    public List<ConditionGroup> Groups { get; set; } = new();
}

/// <summary>
/// A group of conditions combined with a single logical operator.
/// </summary>
public class ConditionGroup
{
    /// <summary>Logical operator combining rules in this group: AND, OR.</summary>
    public string Operator { get; set; } = "AND";

    /// <summary>Individual condition rules in this group.</summary>
    public List<ConditionRule> Rules { get; set; } = new();
}

/// <summary>
/// Individual condition rule: field, operator, value.
/// </summary>
public class ConditionRule
{
    /// <summary>Field identifier from FieldCatalog (e.g., "totalStays", "country").</summary>
    public string FieldId { get; set; } = string.Empty;

    /// <summary>Comparison operator (equals, contains, greaterThan, lessThan, in, etc.).</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>Value to compare against. Type depends on field (number, string, array, etc.).</summary>
    public object? Value { get; set; }
}
