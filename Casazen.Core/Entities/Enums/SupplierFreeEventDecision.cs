namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the supplier answered about an event of its calendar feed that the calendar marks "Available" (free time). The values
/// are stored: never reorder or renumber, only append.
/// </summary>
public enum SupplierFreeEventDecision
{
    /// <summary>Not answered yet: the event keeps its days or hours occupied until the supplier answers.</summary>
    Pending = 0,

    /// <summary>The supplier is available during the event: it occupies nothing and is not asked again.</summary>
    Available = 1,

    /// <summary>The supplier is busy during the event: it occupies its days or hours like any busy event.</summary>
    Busy = 2,
}
