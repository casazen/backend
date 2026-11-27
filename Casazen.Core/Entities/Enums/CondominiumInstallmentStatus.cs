namespace Casazen.Core.Entities.Enums;

/// <summary>Payment state of one owner installment. Stored as an integer: never renumber.</summary>
public enum CondominiumInstallmentStatus
{
    Scheduled = 0,
    PartiallyPaid = 1,
    Paid = 2,
    Cancelled = 3,
}
