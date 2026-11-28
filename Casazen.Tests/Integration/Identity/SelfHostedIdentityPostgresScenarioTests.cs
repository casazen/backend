using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration.Identity;

public sealed class SelfHostedIdentityPostgresScenarioTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [PostgresFact]
    public async Task S1_OnPostgreSql_TheMainPath_AndTheUniqueIndexRefusesASecondRowOfTheSameKey()
    {
        var (stay, logId) = await SelfHostedIdentityScripts.MainPathAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.Database.IsNpgsql());
        var original = await db.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(log => log.Id == logId);
        Assert.Equal(stay.BookingId, original.BookingId);

        // A twin that got past every check of the code: the unique index on the booking and the key is the last line.
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

    [PostgresFact]
    public async Task S5_OnPostgreSql_TheSameRequestTwiceAtOnce_IsOneAttempt()
    {
        await SelfHostedIdentityScripts.DuplicateAsync(factory, atOnce: true);
    }

    [PostgresFact]
    public async Task S5_OnPostgreSql_AnApprovalSentTwiceAtOnce_IsOneApproval()
    {
        await SelfHostedIdentityScripts.ApprovalTwiceAsync(factory, atOnce: true);
    }
}
