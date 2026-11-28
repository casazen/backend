namespace Casazen.Core.Models;

public class Condition
{
    public string Version { get; set; } = "1.0";

    public List<ConditionGroup> Groups { get; set; } = new();
}

public class ConditionGroup
{
    public string Operator { get; set; } = "AND";

    public List<ConditionRule> Rules { get; set; } = new();
}

public class ConditionRule
{
    public string FieldId { get; set; } = string.Empty;

    public string Operator { get; set; } = string.Empty;

    public object? Value { get; set; }
}
