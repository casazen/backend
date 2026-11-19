using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IIstatRegionService" />
/// <remarks>
/// <para><b>Reads.</b> The table is platform data (no tenant filter, the same rows for every org), read whole: it has one row for each region and
/// each of the two autonomous provinces. The region of a comune is the one of the official list (<c>Comuni.RegionIstatCode</c>), and the row of an
/// autonomous province is picked by the plate code of the comune. A comune that is not in the list, or a territory without a row, has no rule:
/// «da verificare», never the row of a neighbour. The property of a coverage is read through the tenant filter of the request, so another org's
/// property is not found.</para>
/// <para><b>The write.</b> One row at a time, by territory, never creating one. The row, its stamp and its audit line
/// (<see cref="RegulatoryDataAuditEntry"/>: who, when, what changed, never a personal value) are saved together or not at all. Logs carry the
/// territory, the kind of change and the staff member's id.</para>
/// </remarks>
public sealed class IstatRegionService(
    AppDbContext db,
    IPropertySenderStore senders,
    IOptions<ComplianceOptions> options,
    ILogger<IstatRegionService> logger,
    TimeProvider? timeProvider = null) : IIstatRegionService
{
    private const string EmptyValue = "(none)";

    private const string HttpsPrefix = "https://";

    /// <summary>
    /// The warning of a day configured for a region in <c>Compliance:IstatDeadlineDays</c> that is not a day of the month. It names the region and the value
    /// and nothing else (no organization, property or person); the day is ignored and the one of the table stands.
    /// </summary>
    internal const string InvalidOverrideMessage =
        "Compliance:IstatDeadlineDays for region {RegionCode} is {ConfiguredDay}, not a day of the month from 1 to 31: ignored, the day of the ISTAT region table is used";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<IstatRegionRule>> ListAsync(CancellationToken ct)
    {
        var rows = await db.IstatRegionRules.AsNoTracking().ToListAsync(ct);
        return rows
            .OrderBy(r => r.RegionIstatCode, StringComparer.Ordinal)
            .ThenBy(r => r.ProvinceCode ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IstatRegionRule> UpdateAsync(IstatRegionRuleUpdate update, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > IstatRegionRule.UserIdMaxLength)
            throw new ArgumentException("The user id of a region change is an id of at most 255 characters.", nameof(userId));

        var regionIstatCode = update.RegionIstatCode?.Trim() ?? string.Empty;
        var provinceCode = string.IsNullOrWhiteSpace(update.ProvinceCode) ? null : update.ProvinceCode.Trim().ToUpperInvariant();
        var rule = await db.IstatRegionRules.FirstOrDefaultAsync(r => r.RegionIstatCode == regionIstatCode && r.ProvinceCode == provinceCode, ct);
        if (rule is null)
            throw TerritoryNotFound();

        var source = HttpsAddress(update.SourceUrl);
        if (source is null)
            throw new DomainRuleException(IstatRegionErrors.SourceInvalidCode, "IstatRegionSourceInvalid");

        string? portal = null;
        if (!string.IsNullOrWhiteSpace(update.PortalUrl))
        {
            portal = HttpsAddress(update.PortalUrl);
            if (portal is null)
                throw new DomainRuleException(IstatRegionErrors.PortalInvalidCode, "IstatRegionPortalInvalid");
        }

        // The API refuses a number that is not a value of the enum; a caller that is not the API must not get one in either.
        if (!Enum.IsDefined(update.DeadlineKind))
            throw new ArgumentOutOfRangeException(nameof(update), update.DeadlineKind, "Not an ISTAT deadline kind.");
        if (!Enum.IsDefined(update.OctorateCoverage))
            throw new ArgumentOutOfRangeException(nameof(update), update.OctorateCoverage, "Not an ISTAT coverage.");

        // Only a monthly deadline has a day, and it has one: the database holds the same rule (CK_IstatRegionRules_Deadline).
        var monthly = update.DeadlineKind == IstatDeadlineKind.Monthly;
        if ((monthly && update.DeadlineDay is not (>= 1 and <= 31)) || (!monthly && update.DeadlineDay is not null))
            throw new DomainRuleException(IstatRegionErrors.DeadlineInvalidCode, "IstatRegionDeadlineInvalid");

        // Null is «not verified» (no field is asked); an empty list is «verified: none required»; a name outside the lists is refused.
        string? requiredFields = null;
        if (update.RequiredGuestFields is { } fields)
        {
            if (!IstatRegionResolver.TryNormalizeFields(fields, out var names))
                throw new DomainRuleException(IstatRegionErrors.FieldsInvalidCode, "IstatRegionFieldsInvalid");

            requiredFields = IstatRegionResolver.FormatFields(names);
        }

        var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);
        var verifiedAt = update.VerifiedAt is { } given ? UtcDateTime.TruncateToMicroseconds(UtcDateTime.Normalize(given)) : now;

        var changes = new List<string>();
        Set(changes, "system", rule.System, Clean(update.System), v => rule.System = v);
        Set(changes, "portalUrl", rule.PortalUrl, portal, v => rule.PortalUrl = v);
        Set(changes, "deadlineKind", rule.DeadlineKind.ToString(), update.DeadlineKind.ToString(), _ => rule.DeadlineKind = update.DeadlineKind);
        Set(changes, "deadlineDay", Text(rule.DeadlineDay), Text(update.DeadlineDay), _ => rule.DeadlineDay = update.DeadlineDay);
        Set(changes, "requiredGuestFields", rule.RequiredGuestFields, requiredFields, v => rule.RequiredGuestFields = v);
        Set(changes, "octorateCoverage", rule.OctorateCoverage.ToString(), update.OctorateCoverage.ToString(), _ => rule.OctorateCoverage = update.OctorateCoverage);
        Set(changes, "sourceUrl", rule.SourceUrl, source, v => rule.SourceUrl = v ?? string.Empty);
        Set(changes, "notes", rule.Notes, Clean(update.Notes), v => rule.Notes = v);

        // Saving a row says the person read it against its source: with nothing else changed, that is the whole change.
        var verified = $"verifiedAt: {Day(rule.VerifiedAt)} -> {Day(verifiedAt)}; source: {source}";
        var action = changes.Count == 0 ? RegulatoryAuditAction.MarkedVerified : RegulatoryAuditAction.Updated;
        rule.VerifiedAt = verifiedAt;
        rule.UpdatedAt = now;
        rule.UpdatedByUserId = userId;

        db.RegulatoryDataAuditEntries.Add(new RegulatoryDataAuditEntry
        {
            EntityType = IstatRegionErrors.AuditEntityType,
            EntityId = rule.Id,
            Action = action,
            ChangedByUserId = userId,
            OccurredAt = now,
            Changes = changes.Count == 0 ? verified : string.Join("; ", changes) + "; " + verified,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "ISTAT region rule {RegionIstatCode}/{ProvinceCode} {Action} by admin {UserId}: coverage {Coverage}, deadline {DeadlineKind}",
            rule.RegionIstatCode, rule.ProvinceCode, action, userId, rule.OctorateCoverage, rule.DeadlineKind);
        return rule;
    }

    public async Task<IstatCoverageView> GetCoverageAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.Id, p.Country, p.ComuneIstatCode })
            .FirstOrDefaultAsync(ct);
        if (property is null)
            throw PropertyNotFound(propertyId);

        // ISTAT follows Alloggiati (ADR-029-R5): the sender is read where it is decided.
        var sender = await senders.GetAsync(property.Id, ct);
        if (!TouristTaxComune.IsItalianCountry(property.Country))
        {
            return new IstatCoverageView(
                property.Id, false, false, null, null, null, null, null, IstatDeadlineKind.Unknown, null, [], false,
                IstatCoverage.Unknown, sender.IstatSender, null, null, null, null);
        }

        var match = await FindForComuneAsync(db, property.ComuneIstatCode, ct);
        var rule = match?.Rule;

        // The deadline of the cockpit: the row's monthly day, or the day configured for the region, which overrides it (a day out of 1 to 31 is ignored).
        var day = IstatRegionResolver.DeadlineDay(
            rule,
            match?.RegionCode,
            options.Value.IstatDeadlineDays,
            (region, configured) => logger.LogWarning(InvalidOverrideMessage, region, configured));
        var kind = day is not null ? IstatDeadlineKind.Monthly : rule?.DeadlineKind ?? IstatDeadlineKind.Unknown;
        var coverage = rule?.OctorateCoverage ?? IstatCoverage.Unknown;
        var next = IstatRegionResolver.NextStep(coverage);

        return new IstatCoverageView(
            property.Id,
            Applies: true,
            RegionKnown: rule is not null,
            match?.RegionIstatCode,
            rule?.ProvinceCode,
            rule?.RegionName,
            rule?.System,
            rule?.PortalUrl,
            kind,
            day,
            IstatRegionResolver.RequiredFields(rule),
            RequiredGuestFieldsKnown: rule?.RequiredGuestFields is not null,
            coverage,
            // The cockpit reads the same rule: where the channel manager does not cover the region, nobody outside sends it and the host does.
            IstatRegionResolver.IstatSenderFor(sender.IstatSender, coverage),
            next,
            next == IstatNextStep.AskSupport ? OnboardingAssistContexts.PanelIstat : null,
            rule?.SourceUrl,
            rule?.VerifiedAt);
    }

    /// <summary>
    /// The row of the table for the comune of a property (null: the property has no comune of the list, or the table has no row for its territory),
    /// with the region of the comune. The same rule for the cockpit, the guest page and the check-in wizard, so they never disagree.
    /// </summary>
    public static async Task<IstatRegionMatch?> FindForComuneAsync(AppDbContext db, string? comuneIstatCode, CancellationToken ct)
    {
        var found = await FindForComuniAsync(db, [comuneIstatCode], ct);
        var code = comuneIstatCode?.Trim();
        return code is not null && found.TryGetValue(code, out var match) ? match : null;
    }

    /// <summary>
    /// The territory and the row of each comune asked for, by ISTAT code of the comune: two reads however many comuni there are (the comuni, and the
    /// table of about twenty rows). A code that is blank or not in the list of the comuni is left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IstatRegionMatch>> FindForComuniAsync(
        AppDbContext db,
        IEnumerable<string?> comuneIstatCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(comuneIstatCodes);
        var codes = comuneIstatCodes
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return new Dictionary<string, IstatRegionMatch>();

        var comuni = await db.Comuni
            .AsNoTracking()
            .Where(c => codes.Contains(c.IstatCode))
            .Select(c => new { c.IstatCode, c.RegionIstatCode, c.ProvinceCode })
            .ToListAsync(ct);
        if (comuni.Count == 0)
            return new Dictionary<string, IstatRegionMatch>();

        var rules = await db.IstatRegionRules.AsNoTracking().ToListAsync(ct);
        return comuni.ToDictionary(
            c => c.IstatCode,
            c => new IstatRegionMatch(
                c.RegionIstatCode,
                c.ProvinceCode,
                ItalianRegions.FindByIstatCode(c.RegionIstatCode)?.Code,
                IstatRegionResolver.Find(rules, c.RegionIstatCode, c.ProvinceCode)),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The address as an absolute https one within the column, or null when it is not one. What it returns satisfies the checks of the table
    /// (<c>LIKE 'https://%'</c>, which is case-sensitive) as well, so an address the database would refuse is a refusal of the service (422) and never a
    /// failure of the database (500): the scheme may be written in any case (RFC 3986), it is stored in lower case, and an address with no <c>//</c> after
    /// the scheme or with no host is not an https address.
    /// </summary>
    private static string? HttpsAddress(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > IstatRegionRule.UrlMaxLength)
            return null;

        if (!trimmed.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var address = HttpsPrefix + trimmed[HttpsPrefix.Length..];
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 ? address : null;
    }

    private static NotFoundException TerritoryNotFound() =>
        new("No row of the ISTAT region table for this territory.") { Code = IstatRegionErrors.NotFoundCode, MessageKey = "IstatRegionNotFound" };

    private static NotFoundException PropertyNotFound(Guid propertyId) =>
        new($"Property {propertyId} not found.") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Day(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? EmptyValue;

    /// <summary>A value for the audit line: «(none)» for null (not set, not verified) and «(empty)» for an empty list.</summary>
    private static string Show(string? value) => value is null ? EmptyValue : value.Length == 0 ? "(empty)" : value;

    /// <summary>Applies a value and notes what it was and is when it changed (the values are codes, addresses and short texts, never a person's).</summary>
    private static void Set(List<string> changes, string field, string? before, string? after, Action<string?> apply)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return;

        changes.Add($"{field}: {Show(before)} -> {Show(after)}");
        apply(after);
    }
}
