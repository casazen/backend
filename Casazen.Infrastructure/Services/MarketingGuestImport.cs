using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed record MarketingImportPreview(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Sample, int TotalRows);
public sealed record MarketingImportError(int Row, string Reason);
public sealed record MarketingImportResult(int Imported, int Skipped, List<MarketingImportError> Errors);

public sealed class MarketingGuestImport(AppDbContext db, ICrmContactIndex index, IOptions<CrmOptions> options, TimeProvider clock)
{
    private static readonly string[] Fields = ["email", "phone", "name", "language", "country", "firstStay", "lastStay", "tags"];

    public static MarketingImportPreview Preview(string filename, byte[] content)
    {
        var table = Read(filename, content);
        return new(table[0].Cells, table.Skip(1).Take(5).Select(row => row.Cells).ToList(), table.Count - 1);
    }

    private static List<ImportTableRow> Read(string filename, byte[] content)
    {
        var table = OnboardingBulkImportService.ReadTable(filename, content);
        if (table.Count < 2) throw new DomainRuleException("import_file_empty", "OnboardingImportFileEmpty");
        return table;
    }

    public async Task<MarketingImportResult> ImportAsync(HostScope scope, Guid propertyId, string filename, byte[] content,
        IReadOnlyDictionary<string, int> mapping, string basis, string source, CancellationToken ct)
    {
        if (!await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).AnyAsync(p => p.Id == propertyId, ct))
            throw new DomainForbiddenException("property_unavailable", "Forbidden");
        var table = Read(filename, content);
        if ((!mapping.ContainsKey("email") && !mapping.ContainsKey("phone"))
            || mapping.Values.Distinct().Count() != mapping.Count
            || mapping.Any(pair => !Fields.Contains(pair.Key) || pair.Value < 0 || pair.Value >= table[0].Cells.Count))
            throw new DomainRuleException("invalid_mapping", "Invalid column mapping");
        var legalBasis = basis switch
        {
            "consent" => GuestContactLegalBasis.Consent,
            "softOptIn" => GuestContactLegalBasis.SoftOptIn,
            "serviceOnly" => GuestContactLegalBasis.ServiceOnly,
            _ => throw new DomainRuleException("invalid_basis", "Invalid legal basis"),
        };
        source = source.Trim();
        if (source.Length is 0 or > GuestContact.BasisVersionMaxLength)
            throw new DomainRuleException("invalid_source", "Invalid evidence source");

        var result = new MarketingImportResult(0, 0, []);
        var candidates = new List<Candidate>();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var row in table.Skip(1))
        {
            string Value(string key) => mapping.TryGetValue(key, out var column) && column < row.Cells.Count ? row.Cells[column].Trim() : "";
            var email = Value("email");
            var phone = Value("phone");
            var normalizedPhone = CrmNormalizer.NormalizePhone(phone, options.Value.DefaultCallingCode);
            var normalizedEmail = "";
            var first = Date(Value("firstStay"));
            var last = Date(Value("lastStay"));
            if ((email.Length == 0 && phone.Length == 0)
                || (email.Length > 0 && !CrmNormalizer.TryNormalizeEmail(email, out normalizedEmail))
                || (phone.Length > 0 && normalizedPhone is null))
                result.Errors.Add(new(row.Number, "invalid_contact"));
            else if ((Value("firstStay").Length > 0 && first is null) || (Value("lastStay").Length > 0 && last is null)
                || first > last || first > now || last > now || (legalBasis == GuestContactLegalBasis.SoftOptIn && last is null))
                result.Errors.Add(new(row.Number, "invalid_stay_dates"));
            else if (Value("language").Length > GuestContact.LanguageMaxLength || Value("country").Length > 100 || Value("name").Length > 250
                || !CrmProfileRules.TryNormalizeTags(Value("tags").Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out var tags))
                result.Errors.Add(new(row.Number, "invalid_profile"));
            else
                candidates.Add(new(normalizedEmail, normalizedPhone, normalizedEmail.Length == 0 ? null : index.Hash(normalizedEmail),
                    normalizedPhone is null ? null : index.Hash(normalizedPhone), Value("name"), Value("language"), Value("country"), first, last, tags));
        }

        var hashes = candidates.SelectMany(c => new[] { c.EmailHash, c.PhoneHash }).OfType<string>().Distinct().ToArray();
        var seen = (await db.GuestContacts.AsNoTracking().Where(c => c.OrgId == scope.OrgId && hashes.Contains(c.ValueHash))
            .Select(c => c.ValueHash).ToListAsync(ct)).ToHashSet();
        // Legacy profiles may already carry a matching phone key without a contact row.
        seen.UnionWith(await db.GuestCrmProfiles.AsNoTracking().Where(p => p.OrgId == scope.OrgId && p.PhoneKeyHash != null && hashes.Contains(p.PhoneKeyHash))
            .Select(p => p.PhoneKeyHash!).ToListAsync(ct));
        var suppressionHashes = hashes.Concat(candidates.Where(c => c.EmailHash != null).Select(c => SuppressionService.LegacyEmailHash(c.Email))).ToArray();
        var suppressed = (await db.SuppressionListEntries.AsNoTracking().Where(s => s.OrgId == scope.OrgId && suppressionHashes.Contains(s.EmailHash))
            .Select(s => s.EmailHash).ToListAsync(ct)).ToHashSet();
        int imported = 0;
        foreach (var row in candidates)
        {
            if ((row.EmailHash != null && seen.Contains(row.EmailHash)) || (row.PhoneHash != null && seen.Contains(row.PhoneHash)))
                continue;
            var profile = new GuestCrmProfile
            {
                OrgId = scope.OrgId, Country = row.Country, Language = row.Language,
                CompositeKeyHash = index.Hash(GuestMatchRules.CompositeKeyText(row.Email, row.Name, "", null, null)),
                PhoneKeyHash = row.PhoneHash, PropertyIdsJson = JsonSerializer.Serialize(new[] { propertyId }),
                TagsJson = CrmProfileRules.SerializeTags(row.Tags), FirstStayAt = row.First ?? row.Last ?? default,
                LastStayAt = row.Last ?? row.First ?? default, TotalStays = row.First != null || row.Last != null ? 1 : 0,
                CreatedAt = now, UpdatedAt = now,
            };
            db.GuestCrmProfiles.Add(profile);
            if (row.EmailHash is not null)
            {
                var relay = OtaForwardAddress.IsOnRelayDomain(row.Email, options.Value.OtaForwardDomains);
                Add(GuestContactChannel.Email, row.Email, row.EmailHash, relay ? GuestContactLegalBasis.ServiceOnly : legalBasis,
                    relay ? GuestContactOrigin.OtaForward : GuestContactOrigin.Imported,
                    suppressed.Contains(row.EmailHash) || suppressed.Contains(SuppressionService.LegacyEmailHash(row.Email)));
            }
            if (row.PhoneHash is not null)
                Add(GuestContactChannel.Phone, row.Phone!, row.PhoneHash, GuestContactLegalBasis.ServiceOnly, GuestContactOrigin.Imported, false);
            imported++;

            void Add(GuestContactChannel channel, string value, string hash, GuestContactLegalBasis contactBasis, GuestContactOrigin origin, bool blocked)
            {
                seen.Add(hash);
                db.GuestContacts.Add(new GuestContact
                {
                    OrgId = scope.OrgId, GuestCrmProfileId = profile.Id, Channel = channel, Value = value, ValueHash = hash,
                    DisplayName = row.Name, Language = row.Language, Origin = origin, LegalBasis = contactBasis,
                    ConsentSource = GuestConsentSource.HistoricalImport, BasisVersion = source, BasisRecordedAt = now,
                    IsSuppressed = blocked, SuppressedSource = blocked ? CrmContactRules.SuppressionSourceList : null,
                    SuppressedAt = blocked ? now : null, CreatedAt = now, UpdatedAt = now,
                });
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex))
        {
            // SaveChanges rolls back the entire import. Re-reading the same file will see the competing commit.
            throw new DomainConflictException("import_concurrent_write", "Import overlaps another write. Retry the same file.");
        }
        return result with { Imported = imported, Skipped = candidates.Count - imported };
    }

    private static DateTime? Date(string raw)
    {
        if (DateTime.TryParseExact(raw, ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        // Excel stores dates as day numbers in the workbook.
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) && serial is > 60 and < 100000)
            return DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Utc);
        return null;
    }

    private sealed record Candidate(string Email, string? Phone, string? EmailHash, string? PhoneHash, string Name, string Language,
        string Country, DateTime? First, DateTime? Last, List<string> Tags);
}
