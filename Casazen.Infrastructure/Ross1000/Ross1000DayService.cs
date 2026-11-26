using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Ross1000;

public sealed record Ross1000Preview(string Hash, int Arrivals, int Departures, string State, int Accepted, int Rejected, IReadOnlyList<Ross1000RecordOutcome> Records);
public sealed record Ross1000Ledger(string Hash, string State, int Accepted, int Rejected, string RecordKeys, string Outcomes);
public sealed class Ross1000DayService(AppDbContext db, IRoss1000DirectRoute route, IConfiguration configuration,
    Ross1000Client client, TimeProvider clock, IHostEnvironment environment)
{
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsProduction() && configuration.GetValue<bool>("Ross1000:Enabled");

    public async Task<Ross1000Preview> RunAsync(Guid org, Guid property, DateOnly day, Ross1000Capacity capacity,
        string? confirmedHash, CancellationToken ct)
    {
        var eligible = await db.Properties.AsNoTracking().AnyAsync(p => p.Id == property && p.OrgId == org &&
            p.Country == "IT" && p.ComuneIstatCode != null && p.ComuneIstatCode.StartsWith("013"), ct);
        if (!eligible) throw new Ross1000Problem("unsupported");
        var enabled = IsEnabled(configuration, environment);
        if (confirmedHash != null && !enabled) throw new Ross1000Problem("disabled");
        await using var guard = confirmedHash == null ? null : await PropertyIstatCredentialsService.LockAsync(db, property, ct);
        var first = await route.EffectiveFromAsync(org, property, ct);
        if ((confirmedHash != null && (!first.HasValue || day < first)) || day > clock.TodayInRomeAsDateOnly()) throw new Ross1000Problem("route_or_day");
        var credentials = await db.PropertyIstatCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.PropertyId == property && c.OrgId == org, ct);
        // A local developer can preview fixture stays without storing dummy portal credentials.
        // The send gate above always rejects Development and Testing, including when LocalPreview is enabled.
        var localPreview = confirmedHash == null && (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
            configuration.GetValue<bool>("Ross1000:LocalPreview");
        string? structure = localPreview ? "CASAZEN-LOCAL" : credentials?.StructureCode;
        if (string.IsNullOrWhiteSpace(structure)) throw new Ross1000Problem("credentials_missing");
        var start = RomeCalendar.ToUtc(day, TimeOnly.MinValue, RomeCalendar.TimeZone);
        var end = RomeCalendar.ToUtc(day.AddDays(1), TimeOnly.MinValue, RomeCalendar.TimeZone);
        // A stay with old, unbackfilled arrival/departure instants needs an operator to reconcile the dates.
        var atDay = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var nextDay = atDay.AddDays(1);
        var candidates = db.Bookings.AsNoTracking().Where(b => b.OrgId == org && b.PropertyId == property &&
            b.NoShowAt == null && (b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut));
        var uncertain = await candidates.AnyAsync(b =>
            (b.ArrivedAt == null && (b.CheckInDate >= atDay && b.CheckInDate < nextDay || b.CheckedOutAt >= start && b.CheckedOutAt < end)) ||
            (b.Status == BookingStatus.CheckedOut && b.CheckedOutAt == null && b.CheckOutDate >= atDay && b.CheckOutDate < nextDay), ct);
        if (uncertain) throw new Ross1000Problem("missing_actual_date");
        var bookings = await candidates.Where(b => (b.ArrivedAt >= start && b.ArrivedAt < end) ||
            (b.CheckedOutAt >= start && b.CheckedOutAt < end)).Include(b => b.StayGuests).ToListAsync(ct);
        var movement = Ross1000MovementBuilder.Build(structure, day, first ?? DateOnly.MinValue, capacity, bookings);
        string keys = string.Join(',', movement.RecordKeys);
        var previous = await db.Database.SqlQuery<Ross1000Ledger>($"""
            SELECT "Hash", "State", "Accepted", "Rejected", "RecordKeys", "Outcomes" FROM "Ross1000Days"
            WHERE "OrgId" = {org} AND "PropertyId" = {property} AND "Day" = {day}
            """).SingleOrDefaultAsync(ct);

        Ross1000Preview Preview(string state, int accepted = 0, int rejected = 0, IReadOnlyList<Ross1000RecordOutcome>? records = null) =>
            new(movement.Hash, movement.Arrivals, movement.Departures, state, accepted, rejected, records ?? []);
        var savedOutcomes = previous == null ? [] : JsonSerializer.Deserialize<List<Ross1000RecordOutcome>>(previous.Outcomes) ?? [];
        if (previous?.State is "sending" or "unknown" or "partial")
            return Preview(previous.State, previous.Accepted, previous.Rejected, savedOutcomes);
        if (previous?.State == "accepted")
        {
            if (previous.Hash == movement.Hash) return Preview("accepted", previous.Accepted, 0, savedOutcomes);
            if (previous.RecordKeys.Split(',', StringSplitOptions.RemoveEmptyEntries).Except(movement.RecordKeys).Any())
                throw new Ross1000Problem("manual_correction");
        }

        if (confirmedHash == null && (!enabled || !first.HasValue || day < first)) return Preview("preview_only");
        if (confirmedHash == null)
            return Preview(previous?.Hash == movement.Hash ? previous.State : previous?.State == "accepted" ? "correction" : "ready");
        if (confirmedHash != movement.Hash) throw new Ross1000Problem("preview_changed");

        // Commit the exclusive reservation before network I/O so a crash, timeout or parallel request cannot resend this day.
        int claimed = previous == null
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Ross1000Days" ("OrgId","PropertyId","Day","Hash","State","RecordKeys")
                VALUES ({org},{property},{day},{movement.Hash},'sending',{keys}) ON CONFLICT DO NOTHING
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Ross1000Days" SET "State" = 'sending', "Hash" = {movement.Hash}, "RecordKeys" = {keys}, "Outcomes" = '[]', "Accepted" = 0, "Rejected" = 0, "UpdatedAt" = now()
                WHERE "OrgId" = {org} AND "PropertyId" = {property} AND "Day" = {day}
                  AND "Hash" = {previous.Hash} AND "State" = {previous.State}
                """, ct);
        if (claimed != 1) throw new Ross1000Problem("already_processing");

        var result = await client.SendAsync(credentials!, movement, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Ross1000Days" SET "State" = {result.State}, "Accepted" = {result.Accepted},
                "Rejected" = {result.Rejected}, "Outcomes" = {JsonSerializer.Serialize(result.Records)}, "UpdatedAt" = now()
            WHERE "OrgId" = {org} AND "PropertyId" = {property} AND "Day" = {day} AND "Hash" = {movement.Hash} AND "State" = 'sending'
            """, CancellationToken.None);
        return Preview(result.State, result.Accepted, result.Rejected, result.Records);
    }
}
