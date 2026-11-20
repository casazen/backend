using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// The Swiss municipalities and localities of the official directory of swisstopo («Amtliches Ortschaftenverzeichnis mit Postleitzahl und
/// Perimeter», E29/F01/US01/T01, NF-124): the seed file compiled into this assembly (<c>Data/Seeds/swiss/swiss-localities.csv</c>, built by
/// <c>scripts/build-swiss-places.mjs</c>; provenance, licence and refresh in <c>Data/Seeds/swiss/README.md</c>), how it is read, and how the
/// <c>SwissMunicipalities</c> and <c>SwissLocalities</c> tables are made equal to it. It replaces the hand-written list of 48 municipalities of
/// 2026-10-24, four of whose five Ticino numbers were wrong (Lugano was 5113, which is Locarno).
/// </summary>
/// <remarks>
/// <para><b>Idempotent.</b> <see cref="ApplyAsync"/> upserts the municipalities by BFS number and removes the ones the file no longer has (a
/// merger, or a row of the old hand-written list) unless a property still carries the number (kept as it is, counted and told to the startup log:
/// the host confirms the new municipality), and adds, corrects or removes the localities by their key; then it records the SHA-256 of the
/// file in <c>OfficialSourceFetches</c> (dataset <see cref="OfficialSourceDatasets.SwisstopoLocalities"/>). With the same file already recorded and
/// the same row counts it changes nothing. Instances starting together are serialized by an advisory lock.</para>
/// <para><b>Strict.</b> The file is data derived from a third party: every line is checked (<see cref="Parse"/>) and a file with one wrong line is
/// refused as a whole, never loaded in part.</para>
/// </remarks>
public static class SwissMunicipalitySeed
{
    /// <summary>The name of the seed compiled into the assembly.</summary>
    public const string ResourceName = "Casazen.Infrastructure.Data.Seeds.swiss.swiss-localities.csv";

    /// <summary>The header line of the seed.</summary>
    public const string Header = "postal_code;add_on;locality;municipality_code;municipality;canton";

    /// <summary>The page of the official directory (the source credit of the licence is «swisstopo»).</summary>
    public const string SourceUrl = "https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis";

    /// <summary>The authority that publishes the directory.</summary>
    public const string Authority = "swisstopo";

    /// <summary>Largest seed read.</summary>
    public const int MaxSeedBytes = 5 * 1024 * 1024;

    private const int Columns = 6;
    private const int MaxNameLength = 100;

    /// <summary>The 26 cantons (ISO 3166-2:CH).</summary>
    public static readonly IReadOnlySet<string> Cantons = new HashSet<string>(StringComparer.Ordinal)
    {
        "AG", "AI", "AR", "BE", "BL", "BS", "FR", "GE", "GL", "GR", "JU", "LU", "NE",
        "NW", "OW", "SG", "SH", "SO", "SZ", "TG", "TI", "UR", "VD", "VS", "ZG", "ZH",
    };

    /// <summary>The seed of this build; <c>null</c> when the build has none.</summary>
    public static byte[]? ReadEmbedded()
    {
        using var stream = typeof(SwissMunicipalitySeed).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Reads a seed: UTF-8, the <see cref="Header"/>, six cells a line, a postal code of four digits (not starting with 0), an add-on of two digits,
    /// a BFS number of one to four digits (not starting with 0), names of 1 to 100 characters, one of the 26 cantons, a municipality with one name
    /// and one canton, no row twice.
    /// </summary>
    /// <exception cref="FormatException">The file is not a seed: the message names the first wrong line.</exception>
    public static SwissPlacesSeedData Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > MaxSeedBytes)
            throw new FormatException($"Swiss places seed: unexpected size of {content.Length} bytes.");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Swiss places seed: the file is not UTF-8.", ex);
        }

        // A byte order mark (U+FEFF) at the start is not part of the header.
        if (text.Length > 0 && text[0] == (char)0xFEFF)
            text = text[1..];

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count < 2 || !string.Equals(lines[0], Header, StringComparison.Ordinal))
            throw new FormatException("Swiss places seed: line 1 is not the header " + Header + ".");

        var municipalities = new Dictionary<string, SwissMunicipality>(StringComparer.Ordinal);
        var localities = new List<SwissLocality>(lines.Count - 1);
        var keys = new HashSet<(string, string, string, string)>();
        for (var i = 1; i < lines.Count; i++)
        {
            var number = i + 1;
            var cells = lines[i].Split(';');
            if (cells.Length != Columns)
                throw Wrong(number, $"{cells.Length} cells instead of {Columns}");

            var (postalCode, addOn, name, municipalityCode, municipalityName, canton) = (cells[0], cells[1], cells[2], cells[3], cells[4], cells[5]);
            if (postalCode.Length != 4 || !postalCode.All(char.IsAsciiDigit) || postalCode[0] == '0')
                throw Wrong(number, "the postal code is not four digits");
            if (addOn.Length != 2 || !addOn.All(char.IsAsciiDigit))
                throw Wrong(number, "the add-on is not two digits");
            if (municipalityCode.Length is < 1 or > 4 || !municipalityCode.All(char.IsAsciiDigit) || municipalityCode[0] == '0')
                throw Wrong(number, "the municipality number is not a BFS number");
            if (!IsName(name) || !IsName(municipalityName))
                throw Wrong(number, "a name is empty, too long or has a control character");
            if (!Cantons.Contains(canton))
                throw Wrong(number, "the canton is not one of the 26");

            if (municipalities.TryGetValue(municipalityCode, out var known))
            {
                if (!string.Equals(known.OfficialName, municipalityName, StringComparison.Ordinal) || !string.Equals(known.CantonCode, canton, StringComparison.Ordinal))
                    throw Wrong(number, $"municipality {municipalityCode} has another name or canton above");
            }
            else
            {
                municipalities[municipalityCode] = new SwissMunicipality { MunicipalityCode = municipalityCode, CantonCode = canton, OfficialName = municipalityName };
            }

            if (!keys.Add((postalCode, addOn, municipalityCode, name)))
                throw Wrong(number, "the row is there twice");

            localities.Add(new SwissLocality
            {
                PostalCode = postalCode,
                AddOn = addOn,
                Name = name,
                NormalizedName = SwissLocality.NormalizeName(name),
                MunicipalityCode = municipalityCode,
                CantonCode = canton,
            });
        }

        return new SwissPlacesSeedData(
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            [.. municipalities.Values.OrderBy(m => m.MunicipalityCode.Length).ThenBy(m => m.MunicipalityCode, StringComparer.Ordinal)],
            localities);
    }

    /// <summary>
    /// Makes <c>SwissMunicipalities</c> and <c>SwissLocalities</c> equal to the seed <paramref name="content"/> (see the remarks of the class) and
    /// records it in <c>OfficialSourceFetches</c>; nothing is written when the same file is already recorded and the row counts match.
    /// </summary>
    /// <exception cref="FormatException">The file is not a seed (<see cref="Parse"/>): nothing is written.</exception>
    public static async Task<SwissPlacesSeedResult> ApplyAsync(AppDbContext db, byte[] content, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        var seed = Parse(content);
        var seedCodes = seed.Municipalities.Select(m => m.MunicipalityCode).ToList();

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SwissPlacesSeed, "swiss-places"));

        var stamp = await db.OfficialSourceFetches.AsNoTracking()
            .Where(f => f.Dataset == OfficialSourceDatasets.SwisstopoLocalities && f.Status == OfficialSourceFetchStatus.Imported)
            .OrderByDescending(f => f.RetrievedAt)
            .Select(f => f.Sha256)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.Equals(stamp, seed.Sha256, StringComparison.Ordinal)
            && await db.SwissMunicipalities.CountAsync(m => seedCodes.Contains(m.MunicipalityCode), cancellationToken) == seed.Municipalities.Count
            && await db.SwissLocalities.CountAsync(cancellationToken) == seed.Localities.Count)
        {
            // Nothing to write. The municipalities an earlier load kept (a property still carries them) are still told to the startup log.
            var kept = await CarriedByPropertiesAsync(
                db, await db.SwissMunicipalities.Where(m => !seedCodes.Contains(m.MunicipalityCode)).Select(m => m.MunicipalityCode).ToListAsync(cancellationToken), cancellationToken);
            return new SwissPlacesSeedResult(false, seed.Sha256, seed.Municipalities.Count, seed.Localities.Count, 0, 0, kept.Count, kept.Values.Sum());
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Municipalities: by BFS number. A number the file no longer has (a merger, a row of the old hand-written list) is removed, unless a property
        // still carries it: that row is kept as it is (below), so the stored place of the property and the canton of its documents do not change under
        // the host's feet, and the host is asked to confirm the new municipality.
        var storedMunicipalities = await db.SwissMunicipalities.ToDictionaryAsync(m => m.MunicipalityCode, StringComparer.Ordinal, cancellationToken);
        foreach (var municipality in seed.Municipalities)
        {
            if (storedMunicipalities.Remove(municipality.MunicipalityCode, out var stored))
            {
                if (!string.Equals(stored.CantonCode, municipality.CantonCode, StringComparison.Ordinal)
                    || !string.Equals(stored.OfficialName, municipality.OfficialName, StringComparison.Ordinal))
                {
                    stored.CantonCode = municipality.CantonCode;
                    stored.OfficialName = municipality.OfficialName;
                    stored.UpdatedAt = now;
                }

                continue;
            }

            db.SwissMunicipalities.Add(new SwissMunicipality
            {
                MunicipalityCode = municipality.MunicipalityCode,
                CantonCode = municipality.CantonCode,
                OfficialName = municipality.OfficialName,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var carried = await CarriedByPropertiesAsync(db, [.. storedMunicipalities.Keys], cancellationToken);
        foreach (var code in carried.Keys)
            storedMunicipalities.Remove(code);

        db.SwissMunicipalities.RemoveRange(storedMunicipalities.Values);

        // Localities: by postal code, add-on, municipality and name; the canton follows the municipality, the search name follows the name.
        var storedLocalities = (await db.SwissLocalities.ToListAsync(cancellationToken))
            .ToDictionary(KeyOf);
        foreach (var locality in seed.Localities)
        {
            if (storedLocalities.Remove(KeyOf(locality), out var stored))
            {
                if (!string.Equals(stored.CantonCode, locality.CantonCode, StringComparison.Ordinal))
                    stored.CantonCode = locality.CantonCode;
                if (!string.Equals(stored.NormalizedName, locality.NormalizedName, StringComparison.Ordinal))
                    stored.NormalizedName = locality.NormalizedName;
                continue;
            }

            db.SwissLocalities.Add(new SwissLocality
            {
                PostalCode = locality.PostalCode,
                AddOn = locality.AddOn,
                Name = locality.Name,
                NormalizedName = locality.NormalizedName,
                MunicipalityCode = locality.MunicipalityCode,
                CantonCode = locality.CantonCode,
            });
        }

        db.SwissLocalities.RemoveRange(storedLocalities.Values);

        db.OfficialSourceFetches.Add(new OfficialSourceFetch
        {
            Dataset = OfficialSourceDatasets.SwisstopoLocalities,
            SourceUrl = SourceUrl,
            Authority = Authority,
            RetrievedAt = now,
            Sha256 = seed.Sha256,
            Status = OfficialSourceFetchStatus.Imported,
            Detail = $"{seed.Municipalities.Count} municipalities, {seed.Localities.Count} localities; removed {storedMunicipalities.Count} municipalities and {storedLocalities.Count} localities"
                + (carried.Count == 0 ? string.Empty : $"; kept {carried.Count} municipalities the file no longer has, still on {carried.Values.Sum()} properties (to be confirmed by the hosts)"),
        });

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return new SwissPlacesSeedResult(
            true, seed.Sha256, seed.Municipalities.Count, seed.Localities.Count, storedMunicipalities.Count, storedLocalities.Count, carried.Count, carried.Values.Sum());
    }

    /// <summary>
    /// How many properties carry each of <paramref name="codes"/> as their Swiss municipality: every org and the deleted properties too (the load runs
    /// outside a request, for the whole platform), only the codes that some property carries are in the answer.
    /// </summary>
    private static async Task<Dictionary<string, int>> CarriedByPropertiesAsync(
        AppDbContext db, List<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var carried = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.SwissMunicipalityCode != null && codes.Contains(p.SwissMunicipalityCode))
            .Select(p => p.SwissMunicipalityCode!)
            .ToListAsync(cancellationToken);
        return carried.GroupBy(code => code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Called by the Up method of the migration <c>AddSwissMunicipality</c> (2026-10-25), which inserted here a hand-written list of 48
    /// municipalities, four of its five Ticino numbers wrong. It inserts nothing any more: a new database gets the official directory from
    /// <see cref="ApplyAsync"/> at startup (<c>SwissPlacesSeedHostedService</c>), and a database that already has the old rows gets them corrected
    /// or removed by the same load. The migration file itself is left as it was generated.
    /// </summary>
    public static void Insert(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
    }

    /// <summary>
    /// Deletes the Swiss municipalities. Called by the Down method of the migration <c>AddSwissMunicipality</c>, with raw SQL so that rolling back
    /// through it never needs a model where the table does not exist yet.
    /// </summary>
    public static void Delete(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("DELETE FROM \"SwissMunicipalities\";");
    }

    private static bool IsName(string value) =>
        value.Length is >= 1 and <= MaxNameLength
        && value.Trim().Length == value.Length
        && !value.Any(char.IsControl);

    private static (string, string, string, string) KeyOf(SwissLocality locality) =>
        (locality.PostalCode, locality.AddOn, locality.MunicipalityCode, locality.Name);

    private static FormatException Wrong(int line, string reason) => new($"Swiss places seed: line {line}: {reason}.");
}

/// <summary>A seed read by <see cref="SwissMunicipalitySeed.Parse"/>: its SHA-256, the municipalities (one per BFS number) and the localities.</summary>
public sealed record SwissPlacesSeedData(string Sha256, IReadOnlyList<SwissMunicipality> Municipalities, IReadOnlyList<SwissLocality> Localities);

/// <summary>
/// What <see cref="SwissMunicipalitySeed.ApplyAsync"/> did: <see cref="Imported"/> is false when the same file was already loaded (nothing written);
/// the counts are the ones of the file and the rows removed because the file no longer has them. <see cref="MunicipalitiesKept"/> is the number of
/// municipalities the file no longer has that stay because a property still carries them (<see cref="PropertiesOnKept"/> properties, to be confirmed
/// by their hosts); it is told on every load, also when nothing was written.
/// </summary>
public sealed record SwissPlacesSeedResult(
    bool Imported,
    string Sha256,
    int Municipalities,
    int Localities,
    int MunicipalitiesRemoved,
    int LocalitiesRemoved,
    int MunicipalitiesKept = 0,
    int PropertiesOnKept = 0);
