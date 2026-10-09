using System.Text.RegularExpressions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-11: what the customer's own area of a booking asks the database for, read as the SQL the PostgreSQL provider makes of it (no
/// server needed). The privacy of the view rests on these statements: the requests are scoped by the supplier org and the showcase
/// context, only the columns the customer may read are selected, and the encrypted place comes from a statement of its own.
/// </summary>
public class ShowcaseBookingManagerSqlTests
{
    [Fact]
    public void TheBookingsOfASupplier_AreScopedByTheSupplierOrgAndTheShowcaseContext_AndSelectOnlyWhatTheCustomerMayRead()
    {
        // With the encryption on, as in production.
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseBookingManager.RowsOf(db, Guid.NewGuid()).ToQueryString());

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"RentalContext\" = 2", sql);
        // What it reads: the status, the code, the service, the time, the price, the deadlines, the proposal, the cancellation, the comune.
        foreach (var column in new[]
                 {
                     "Status", "PublicCode", "ServiceNameSnapshot", "ScheduledStartUtc", "ScheduledEndUtc", "EstimatedAmountCents", "QuotedAmountCents",
                     "FinalAmountCents", "PriceLinesJson", "OptionsJson", "ResponseDueAt", "ProposedStartUtc", "ProposedEndUtc", "ProposedAt",
                     "ProposalMessage", "CancelledAt", "CancelledBy", "CancellationReason", "RejectionReason", "LocationCity", "LocationPostalCode",
                 })
        {
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        }

        // What it never reads: the encrypted place, what the supplier noted for itself, who took the request, the photos, the host's
        // notes, the stay, the property, the supplier's org twice, the version of the row.
        foreach (var column in new[]
                 {
                     "LocationAddress", "LocationFloor", "LocationAccessNotes", "CompletionNotes", "WorkPhotosJson", "TakenByUserId", "ProposedByUserId",
                     "Notes", "PropertyId", "BookingId", "LastRemindedAt", "ReminderSentAt", "PaidAt", "ChargeToGuest",
                 })
        {
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        }

        // No other table: the customer's contact comes from its own, scoped statement.
        Assert.DoesNotContain("\"ServiceCustomers\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExactPlace_ComesFromAStatementOfItsOwn_ScopedBySupplierRequestAndContext()
    {
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseBookingManager.LocationOf(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString());

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"Id\" = @", sql);
        Assert.Matches("\"RentalContext\" = 2", sql);
        foreach (var column in new[] { "LocationAddress", "LocationFloor", "LocationAccessNotes" })
            Assert.Single(Regex.Matches(sql, $"\"{column}\""));
        foreach (var column in new[] { "LocationCity", "CompletionNotes", "Notes", "Status", "PublicCode" })
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupplier_IsFoundBySlugOnly_AndYieldsTheOrgItsStatusItsNameAndItsSlug()
    {
        using var db = NewNpgsqlContext();

        var sql = Normalize(ShowcaseBookingManager.SupplierBySlugOf(db, "vetrina-test").ToQueryString());

        Assert.Matches("\"ShowcaseSlug\" = ", sql);
        foreach (var column in new[] { "OrgId", "Status", "LegalName", "ShowcaseSlug" })
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        // Not its contacts, its VAT number, its calendar, its tokens.
        foreach (var column in new[] { "Email", "Phone", "VatNumber", "IcalFeedUrl", "GoogleCalendarRefreshToken", "ClaimTokenHash", "SuspensionReason", "Bio" })
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        // The status is not a predicate: a customer finds its booking of a supplier that was suspended meanwhile.
        Assert.DoesNotContain("\"Status\" =", sql, StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ");

    /// <summary>A context on the Npgsql provider that never connects; <paramref name="encrypted"/> gives it the encryption of the columns.</summary>
    private static AppDbContext NewNpgsqlContext(bool encrypted = false) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenantContext: null,
            encrypted ? new EphemeralDataProtectionProvider() : null);
}
