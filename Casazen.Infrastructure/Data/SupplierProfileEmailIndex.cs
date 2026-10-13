using Npgsql;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// One supplier profile per email (SU-14, A4-22): unique index <see cref="Name"/> on <c>lower(btrim("Email"))</c> of
/// <c>SupplierProfiles</c>, blank emails excluded. It is created by the migration <c>SupplierProfileEmailUnique</c> with
/// raw SQL (EF Core cannot model an expression index), so it is not part of the EF model.
/// </summary>
internal static class SupplierProfileEmailIndex
{
    public const string Name = "UIX_SupplierProfiles_NormalizedEmail";

    /// <summary>The value the index compares: trimmed, lower case. Empty for a blank email, which is not unique.</summary>
    public static string Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    /// <summary>True when <paramref name="exception"/> (or an inner one) is the unique violation of this index.</summary>
    public static bool IsViolation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
                && string.Equals(postgres.ConstraintName, Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
