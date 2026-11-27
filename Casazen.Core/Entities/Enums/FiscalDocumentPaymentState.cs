namespace Casazen.Core.Entities.Enums;

/// <summary>Whether the amount of a fiscal document was collected (FIS-04, FIS-05, E06/F01/US02/T01).</summary>
public enum FiscalDocumentPaymentState
{
    /// <summary>Not known (an imported document that says nothing).</summary>
    Unknown = 0,

    /// <summary>Still to collect.</summary>
    ToCollect = 1,

    /// <summary>Collected.</summary>
    Paid = 2,
}
