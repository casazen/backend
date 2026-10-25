using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-26: Routine automation proof tests for guardrails (ADR-022-R11).
/// These tests prove that critical guardrails (access code after ID verification, no Airbnb contact before booking)
/// are non-bypassable, even if configured incorrectly.
/// </summary>
public class RoutineGuardrailProofTests
{
    /// <summary>
    /// Proof test for AC-NF26-4 (ADR-022-R11): proves that SendAccessCode action is blocked when ID verification
    /// is not completed. Without this check, guests could receive access codes without identity verification,
    /// violating security and potentially regulatory requirements.
    /// </summary>
    [Fact]
    public async Task Routine_AccessCodeGuardrail()
    {
        // Arrange: Set up in-memory database with a routine that tries to send access code
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        // Create a routine that attempts to send access code
        var routine = new Routine
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Send Access Code on Booking Confirmed",
            EventType = RoutineEventType.BookingConfirmed,
            Active = true,
            TestMode = false,
            ActionsJson = JsonSerializer.Serialize(new[]
            {
                new { Type = RoutineActionType.SendAccessCode, Config = new { } }
            })
        };

        await using (var context = new AppDbContext(options))
        {
            context.Routines.Add(routine);
            await context.SaveChangesAsync();
        }

        // Act: Trigger the routine with a payload indicating ID is NOT verified
        await using (var context = new AppDbContext(options))
        {
            var mockLogger = new Mock<ILogger<RoutineService>>();
            var service = new RoutineService(context, mockLogger.Object);

            var payload = new
            {
                BookingId = bookingId,
                IdVerified = false  // CRITICAL: ID not verified yet
            };

            await service.TriggerAsync(
                RoutineEventType.BookingConfirmed,
                $"booking-{bookingId}",
                payload,
                CancellationToken.None);
        }

        // Assert: Verify that execution was created but action was blocked
        await using (var context = new AppDbContext(options))
        {
            var execution = await context.RoutineExecutions
                .FirstOrDefaultAsync(e => e.RoutineId == routine.Id);

            Assert.NotNull(execution);
            Assert.Equal("Success", execution.Outcome); // Execution completed

            var result = JsonSerializer.Deserialize<RoutineExecutionResult>(execution.ResultJson);
            Assert.NotNull(result);

            // The critical assertion: access code action was BLOCKED, not executed
            Assert.Contains(result.Errors, e => e.Contains("Access code can only be sent after ID verification"));

            // Verify no successful SendAccessCode action was recorded
            Assert.DoesNotContain(result.ExecutedActions, a => a.Contains("SendAccessCode") && !a.Contains("blocked"));
        }
    }

    /// <summary>
    /// Proof test for AC-NF26-5 (ADR-022-R11): proves that MessageToGuest action is blocked for Airbnb bookings
    /// before confirmation. Without this check, the org could violate Airbnb's terms of service by contacting
    /// guests through external channels before the booking is confirmed.
    /// </summary>
    [Fact]
    public async Task Routine_AirbnbGuardrail()
    {
        // Arrange: Set up in-memory database with a routine that tries to message guest
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        // Create a routine that attempts to send a message
        var routine = new Routine
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Welcome Message on Booking New",
            EventType = RoutineEventType.BookingNew,
            Active = true,
            TestMode = false,
            ActionsJson = JsonSerializer.Serialize(new[]
            {
                new { Type = RoutineActionType.MessageToGuest, Config = new { TemplateId = "welcome" } }
            })
        };

        await using (var context = new AppDbContext(options))
        {
            context.Routines.Add(routine);
            await context.SaveChangesAsync();
        }

        // Act: Trigger the routine with a payload for an unconfirmed Airbnb booking
        await using (var context = new AppDbContext(options))
        {
            var mockLogger = new Mock<ILogger<RoutineService>>();
            var service = new RoutineService(context, mockLogger.Object);

            var payload = new
            {
                BookingId = bookingId,
                Channel = "Airbnb",          // CRITICAL: Airbnb booking
                BookingConfirmed = false     // CRITICAL: Not confirmed yet
            };

            await service.TriggerAsync(
                RoutineEventType.BookingNew,
                $"booking-{bookingId}",
                payload,
                CancellationToken.None);
        }

        // Assert: Verify that execution was created but action was blocked
        await using (var context = new AppDbContext(options))
        {
            var execution = await context.RoutineExecutions
                .FirstOrDefaultAsync(e => e.RoutineId == routine.Id);

            Assert.NotNull(execution);
            Assert.Equal("Success", execution.Outcome);

            var result = JsonSerializer.Deserialize<RoutineExecutionResult>(execution.ResultJson);
            Assert.NotNull(result);

            // The critical assertion: Airbnb contact was BLOCKED before confirmation
            Assert.Contains(result.Errors, e => e.Contains("Cannot contact Airbnb guests before booking is confirmed"));

            // Verify no successful MessageToGuest action was recorded
            Assert.DoesNotContain(result.ExecutedActions, a => a.Contains("MessageToGuest") && !a.Contains("blocked"));
        }
    }

    /// <summary>
    /// Helper class to deserialize execution results for assertions.
    /// </summary>
    private class RoutineExecutionResult
    {
        public List<string> ExecutedActions { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }
}
