using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration.Identity;

/// <summary>
/// E74/F02/US01/T02 (NF-188, NF-194, NF-195, decision D8): the PostgreSQL twins of the main path (S1) and of the duplicate (S5) of the acceptance scenario, never run on the InMemory fallback
/// of a machine without a database (<see cref="PostgresFactAttribute"/>): the real row lock of the session and the unique index of the log, which InMemory knows nothing of, are what
/// makes a request sent twice at once one attempt, and the database is the last line of that rule, so the twin proves the database says no when the code does not.
/// </summary>
public sealed class SelfHostedIdentityPostgresScenarioTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    /// <summary>S1 on PostgreSQL: the main path of the observation, and the database refuses a second row of the log with the key of the attempt</summary>
    [PostgresFact]
    public async Task S1_OnPostgreSql_TheMainPath_AndTheUniqueIndexRefusesASecondRowOfTheSameKey()
    {
        var (stay, logId) = await SelfHostedIdentityScripts.MainPathAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.Database.IsNpgsql());
        var original = await db.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(log => log.Id == logId);
        Assert.Equal(stay.BookingId, original.BookingId);

        // A twin of the attempt that got past every check of the code: the unique index on the booking and the key is the last line, and it says no.
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = original.OrgId,
            BookingId = original.BookingId,
            GuestId = original.GuestId,
            Outcome = BiometricOutcome.Escalate,
            Method = VerificationMethod.Biometric,
            Attempts = 2,
            ModelName = "scenario",
            ModelVersion = "1",
            DecisionMode = IdentityDecisionMode.Observation,
            IdempotencyKey = original.IdempotencyKey,
        });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var violation = Assert.IsType<PostgresException>(refused.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, BiometricVerificationLog.IdempotencyIndexName), (violation.SqlState, violation.ConstraintName));
    }

    /// <summary>S5 on PostgreSQL: the same request sent twice at once is one attempt, one row, one To-do, one push, one set of evidence</summary>
    [PostgresFact]
    public async Task S5_OnPostgreSql_TheSameRequestTwiceAtOnce_IsOneAttempt()
    {
        await SelfHostedIdentityScripts.DuplicateAsync(factory, atOnce: true);
    }

    /// <summary>S5 on PostgreSQL: an approval sent twice at once is one approval: the same body and lock code, one row, one event</summary>
    [PostgresFact]
    public async Task S5_OnPostgreSql_AnApprovalSentTwiceAtOnce_IsOneApproval()
    {
        await SelfHostedIdentityScripts.ApprovalTwiceAsync(factory, atOnce: true);
    }
}
