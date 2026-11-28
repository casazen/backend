using System.Text.RegularExpressions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
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
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseBookingManager.RowsOf(db, Guid.NewGuid()).ToQueryString());

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"RentalContext\" = 2", sql);
        foreach (var column in new[]
                 {
                     "Status", "PublicCode", "ServiceNameSnapshot", "ScheduledStartUtc", "ScheduledEndUtc", "EstimatedAmountCents", "QuotedAmountCents",
                     "FinalAmountCents", "PriceLinesJson", "OptionsJson", "ResponseDueAt", "ProposedStartUtc", "ProposedEndUtc", "ProposedAt",
                     "ProposalMessage", "CancelledAt", "CancelledBy", "CancellationReason", "RejectionReason", "LocationCity", "LocationPostalCode",
                     "QuoteSentAt", "QuoteLinesJson", "QuoteTotalCents", "QuoteNote", "QuoteScheduledStartUtc", "QuoteScheduledEndUtc",
                     "PaymentMode",
                 })
        {
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        }

        foreach (var column in new[]
                 {
                     "LocationAddress", "LocationFloor", "LocationAccessNotes", "CompletionNotes", "WorkPhotosJson", "TakenByUserId", "ProposedByUserId",
                     "Notes", "PropertyId", "BookingId", "LastRemindedAt", "ReminderSentAt", "PaidAt", "ChargeToGuest",
                     "QuoteSentByUserId", "QuoteEventsJson",
                 })
        {
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        }

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
    public void TheTraceOfTheOffers_ComesFromAStatementOfItsOwn_ScopedBySupplierRequestAndContext_AndReadsNothingElse()
    {
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseBookingManager.QuoteEventsOf(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString());

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"Id\" = @", sql);
        Assert.Matches("\"RentalContext\" = 2", sql);
        Assert.Single(Regex.Matches(sql, "\"QuoteEventsJson\""));
        foreach (var column in new[] { "QuoteSentAt", "QuoteTotalCents", "QuoteLinesJson", "Status", "PublicCode", "LocationCity", "LocationAddress", "CustomerId" })
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBasisOfTheAlternatives_IsOneRequestOfTheSupplier_AndReadsOnlyTheCategoryAndTheComune()
    {
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseBookingManager.AlternativesBasisOf(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString());

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"Id\" = @", sql);
        Assert.Matches("\"RentalContext\" = 2", sql);
        foreach (var column in new[] { "Category", "LocationComuneIstat", "LocationCity" })
            Assert.Single(Regex.Matches(sql, $"\"{column}\""));
        foreach (var column in new[]
                 {
                     "LocationAddress", "LocationFloor", "LocationAccessNotes", "LocationPostalCode", "Notes", "CustomerId", "PublicCode", "Status", "QuoteEventsJson",
                 })
        {
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRequestsWaitingForTheirSuppliersFirstAnswer_AreListedWithTheFewColumnsTheRuleNeeds_AndNothingTheCustomerWrote()
    {
        using var db = NewNpgsqlContext(encrypted: true);

        var sql = Normalize(ShowcaseReplyReminderService.WaitingOf(db, DateTime.UtcNow).ToQueryString());

        Assert.Matches("\"RentalContext\" = 2", sql);
        Assert.Contains("\"Kind\" = ", sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\" = ", sql, StringComparison.Ordinal);
        Assert.Matches("\"ResponseDueAt\" > @", sql);
        Assert.Contains("\"QuoteSentAt\" IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"ReplyReminderLastSentAt\" IS NULL", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "Id", "CreatedAt", "ResponseDueAt", "ReplyReminderHalfSentAt" })
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);

        foreach (var column in new[]
                 {
                     "LocationAddress", "LocationFloor", "LocationAccessNotes", "LocationCity", "Notes", "CustomerId", "PublicCode", "ProposalMessage",
                     "QuoteNote", "QuoteLinesJson", "WorkPhotosJson",
                 })
        {
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSupplier_IsFoundBySlugOnly_AndYieldsTheOrgItsStatusItsNameAndItsSlug()
    {
        using var db = NewNpgsqlContext();

        var sql = Normalize(ShowcaseBookingManager.SupplierBySlugOf(db, "vetrina-test").ToQueryString());

        Assert.Matches("\"ShowcaseSlug\" = ", sql);
        foreach (var column in new[] { "OrgId", "Status", "LegalName", "ShowcaseSlug" })
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "Email", "Phone", "VatNumber", "IcalFeedUrl", "GoogleCalendarRefreshToken", "ClaimTokenHash", "SuspensionReason", "Bio" })
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
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
            encrypted ? TestDataProtection.Shared : null);
}
