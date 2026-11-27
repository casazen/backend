using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the CRM services say about a failed save, and how they tell a unique index that refused a row from any other error (E07/F01/US02). The
/// message of a database error can carry the values of the row (<c>Key (...)=(...) already exists</c>), and a CRM row is personal data or a keyed
/// hash of it: a log line gets the type of the error, its SQLSTATE and the name of the constraint, never the message.
/// </summary>
internal static class CrmDatabaseErrors
{
    /// <summary>Names the error for a log: <c>DbUpdateException/23505/UIX_GuestContacts_OrgId_Channel_ValueHash</c>, or the type alone.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var cause = exception is DbUpdateException { InnerException: { } inner } ? inner : exception;
        return cause is PostgresException postgres
            ? $"{exception.GetType().Name}/{postgres.SqlState}/{postgres.ConstraintName}"
            : exception.GetType().Name;
    }

    /// <summary>True when PostgreSQL refused the save because a unique index already holds the row; <paramref name="constraintName"/> narrows it to one index.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string? constraintName = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && (constraintName is null || string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal));
    }
}
