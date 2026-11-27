namespace Casazen.Core.Entities;

/// <summary>
/// A row that is evidence (E06/F05/US02/T02, AC-NF110-1): once saved it is never updated or deleted through the DbContext, which refuses the save
/// (409 <c>owner_ledger_immutable</c>). A correction is a new row that reverses it.
/// </summary>
#pragma warning disable CA1040 // A marker: the DbContext reads it, there is nothing to implement.
public interface IAppendOnlyRecord
{
}
#pragma warning restore CA1040
