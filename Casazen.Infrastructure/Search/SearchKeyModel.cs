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
    /// <summary>Name of the generated column, the same on every searchable table.</summary>
    public const string KeyProperty = "SearchKey";

    /// <summary>Text search configuration of the indexes and of the queries: no stemming, no stop words.</summary>
    public const string TextSearchConfig = "simple";

    /// <summary>Index over the prefix of the booking code (<c>varchar_pattern_ops</c>, so that <c>LIKE 'ABC%'</c> is a range scan in any collation).</summary>
    public const string BookingCodePrefixIndex = "IX_Bookings_BookingCode_Prefix";

    /// <summary>
    /// The SQL expression that folds <paramref name="textExpression"/> exactly as <see cref="SearchText.Fold"/> does: the accents of
    /// <see cref="SearchFolding"/> to plain letters (<c>translate</c>, one letter each; <c>replace</c> for the ones that become two),
    /// lower case, every run of characters that are not <c>a-z0-9</c> to one space, no space at the ends. Built from the table, so the
    /// two cannot differ; <c>SearchFoldingTests</c> runs the same steps in C# and compares them for the whole range of the table.
    /// All the functions are immutable, as a generated column requires.
    /// </summary>
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

    /// <summary>Key of a property: name, city and CIN.</summary>
    public static string PropertyKeySql { get; } =
        FoldSql("\"Name\" || ' ' || \"City\" || ' ' || coalesce(\"CinCode\", '')");

    /// <summary>Key of a guest: surname, first name and e-mail address (the e-mail is only searched, the answer shows it masked).</summary>
    public static string GuestKeySql { get; } =
        FoldSql("\"LastName\" || ' ' || \"FirstName\" || ' ' || \"Email\"");

    /// <summary>Key of a party of a lease: surname and first name.</summary>
    public static string PartyKeySql { get; } =
        FoldSql("\"LastName\" || ' ' || \"FirstName\"");

    /// <summary>
    /// Key of a service request: the name of the service as the supplier wrote it, the category code, the code the customer of a
    /// showcase request quotes and the comune of the work of such a request.
    /// </summary>
    public static string ServiceRequestKeySql { get; } =
        FoldSql("coalesce(\"ServiceNameSnapshot\", '') || ' ' || \"Category\" || ' ' || coalesce(\"PublicCode\", '') || ' ' || coalesce(\"LocationCity\", '')");

    /// <summary>Key of a supplier profile: the business name.</summary>
    public static string SupplierProfileKeySql { get; } =
        FoldSql("\"LegalName\"");

    /// <summary>The keys and the index of each searchable table, for the tests that check them all.</summary>
    public static IReadOnlyList<(Type Entity, string Table, string KeySql, string IndexName)> Keys { get; } =
    [
        (typeof(Property), "Properties", PropertyKeySql, "IX_Properties_SearchKey_Fts"),
        (typeof(Guest), "Guests", GuestKeySql, "IX_Guests_SearchKey_Fts"),
        (typeof(Party), "Parties", PartyKeySql, "IX_Parties_SearchKey_Fts"),
        (typeof(ServiceRequest), "ServiceRequests", ServiceRequestKeySql, "IX_ServiceRequests_SearchKey_Fts"),
        (typeof(SupplierProfile), "SupplierProfiles", SupplierProfileKeySql, "IX_SupplierProfiles_SearchKey_Fts"),
    ];

    /// <summary>Adds the generated key and its full-text index to every searchable entity, and the booking code prefix index.</summary>
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

        // "Cerca per codice": the booking code is stored upper case without separator, so a prefix of it is a range of the index.
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
