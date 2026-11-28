using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Search;

/// <summary>
/// The search keys of the global search (UI-13a): for each searchable table, a stored generated column <c>SearchKey</c> that holds
/// the words of the row's main texts folded like <see cref="SearchText.Fold"/> (lower case, no accents, words of <c>a-z0-9</c> separated
/// by one space), and a full-text (GIN) index over it. The database computes the key, so every writer of the table is covered (the
/// application, a SQL backfill, an anonymization) and the key can never be stale: when a guest is erased its key follows.
/// </summary>
/// <remarks>
/// <para><b>Why this and not an extension.</b> <c>pg_trgm</c> would index "contains"; it is not used because the environments of the
/// application share one Supabase database, one schema each (<c>docs/INFRA.md</c>): an extension is created once per database, in
/// the first schema of the search path of the connection that creates it, so the second environment would not find its operator
/// classes and its migration would fail. The built-in full-text search needs no extension: the key is indexed as a
/// <c>tsvector</c> of the <c>simple</c> configuration (no stemming, no stop words, so a name is a name in any language) and a term is
/// a prefix query on it (<c>to_tsvector('simple', "SearchKey") @@ to_tsquery('simple', 'ros:* &amp; mar:*')</c>). The same expression is
/// in the index and in the query, so the planner uses the index (<c>GlobalSearchPostgresTests</c> proves it with EXPLAIN).</para>
/// <para><b>Not in the model.</b> The key is a shadow property: the entities of the Core know nothing of the search. In tests with the
/// in-memory provider, which has no generated columns, the key is written by the test (<c>SearchKeysForTests</c>) with the same
/// <see cref="SearchText.Fold"/>; a PostgreSQL test compares the key the database computes with it for the same rows.</para>
/// </remarks>
internal static class SearchKeyModel
{
    public const string KeyProperty = "SearchKey";

    public const string TextSearchConfig = "simple";

    /// <summary>Index over the prefix of the booking code (<c>varchar_pattern_ops</c>, so that <c>LIKE 'ABC%'</c> is a range scan in any collation).</summary>
    public const string BookingCodePrefixIndex = "IX_Bookings_BookingCode_Prefix";

    public static string FoldSql(string textExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textExpression);

        var expression = new StringBuilder();
        expression.Append("translate(").Append(textExpression).Append(", ")
            .Append(Literal(SearchFolding.TranslateFrom)).Append(", ").Append(Literal(SearchFolding.TranslateTo)).Append(')');

        foreach (var (character, letters) in SearchFolding.PairReplacements)
        {
            expression.Insert(0, "replace(");
            expression.Append(", ").Append(Literal(character.ToString())).Append(", ").Append(Literal(letters)).Append(')');
        }

        return "btrim(regexp_replace(lower(" + expression + "), '[^a-z0-9]+', ' ', 'g'))";
    }

    public static string PropertyKeySql { get; } =
        FoldSql("\"Name\" || ' ' || \"City\" || ' ' || coalesce(\"CinCode\", '')");

    public static string GuestKeySql { get; } =
        FoldSql("\"LastName\" || ' ' || \"FirstName\" || ' ' || \"Email\"");

    public static string PartyKeySql { get; } =
        FoldSql("\"LastName\" || ' ' || \"FirstName\"");

    public static string ServiceRequestKeySql { get; } =
        FoldSql("coalesce(\"ServiceNameSnapshot\", '') || ' ' || \"Category\" || ' ' || coalesce(\"PublicCode\", '') || ' ' || coalesce(\"LocationCity\", '')");

    public static string SupplierProfileKeySql { get; } =
        FoldSql("\"LegalName\"");

    public static IReadOnlyList<(Type Entity, string Table, string KeySql, string IndexName)> Keys { get; } =
    [
        (typeof(Property), "Properties", PropertyKeySql, "IX_Properties_SearchKey_Fts"),
        (typeof(Guest), "Guests", GuestKeySql, "IX_Guests_SearchKey_Fts"),
        (typeof(Party), "Parties", PartyKeySql, "IX_Parties_SearchKey_Fts"),
        (typeof(ServiceRequest), "ServiceRequests", ServiceRequestKeySql, "IX_ServiceRequests_SearchKey_Fts"),
        (typeof(SupplierProfile), "SupplierProfiles", SupplierProfileKeySql, "IX_SupplierProfiles_SearchKey_Fts"),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var (entity, _, keySql, indexName) in Keys)
        {
            // Nullable in the model on purpose: the database never leaves it null (every input of the expression is NOT NULL or
            // coalesced), but a required shadow property would make the in-memory provider, which has no generated columns, refuse
            // every row that is saved without a key, so every test that saves a property or a guest would fail.
            var builder = modelBuilder.Entity(entity);
            builder.Property<string>(KeyProperty)
                .HasColumnType("text")
                .HasComputedColumnSql(keySql, stored: true);
            builder.HasIndex(KeyProperty)
                .HasDatabaseName(indexName)
                .HasMethod("gin")
                .IsTsVectorExpressionIndex(TextSearchConfig);
        }

        modelBuilder.Entity<Booking>()
            .HasIndex(b => b.BookingCode)
            .HasDatabaseName(BookingCodePrefixIndex)
            .HasOperators("varchar_pattern_ops");
    }

    /// <summary>A SQL string literal: plain for ASCII, a Unicode escape string (<c>U&amp;'\00DF'</c>) otherwise, so the SQL text is ASCII only.</summary>
    private static string Literal(string value)
    {
        if (value.All(character => character < 128))
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        var literal = new StringBuilder("U&'");
        foreach (var character in value)
        {
            if (character == '\\')
                literal.Append(@"\\");
            else if (character == '\'')
                literal.Append("''");
            else if (character < 128)
                literal.Append(character);
            else
                literal.Append('\\').Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return literal.Append('\'').ToString();
    }
}
