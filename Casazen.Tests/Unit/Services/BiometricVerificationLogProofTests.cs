using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof tests for BiometricVerificationLog (NF-11, ADR-013): proves privacy / data rules.
/// </summary>
public class BiometricVerificationLogProofTests
{
    /// <summary>
    /// ADR-013-R2 / ADR-013-R3 / AC-NF11-12: BiometricVerificationLog MUST NEVER store images or embeddings
    /// per the immediate deletion guarantee. Selfie images, cropped faces, and face vectors MUST be
    /// in-memory only, deleted in finally block before response, NEVER passed to Hangfire, NEVER in logs.
    /// Only the outcome, attempts, model metadata, and thresholds are persisted.
    /// </summary>
    [Fact]
    public void BiometricVerificationLog_NoImages_NeverStored()
    {
        // Arrange: Create a BiometricVerificationLog entry
        var log = new BiometricVerificationLog
        {
            OrgId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            Outcome = BiometricOutcome.Accept,
            Attempts = 1,
            ModelName = "AuraFace-v1",
            ModelVersion = "1.0.0",
            ThresholdsJson = "{\"AcceptThreshold\": 0.85, \"EscalateThreshold\": 0.70}",
            CreatedAt = DateTime.UtcNow,
        };

        // Act & Assert: Verify BiometricVerificationLog entity structure enforces no-image-storage rule
        var logType = typeof(BiometricVerificationLog);
        var allProperties = logType.GetProperties();

        // ADR-013-R2 / ADR-013-R3: Log MUST have outcome, attempts, model metadata
        Assert.NotNull(logType.GetProperty("Outcome"));
        Assert.Equal(typeof(BiometricOutcome), logType.GetProperty("Outcome")!.PropertyType);

        Assert.NotNull(logType.GetProperty("Attempts"));
        Assert.Equal(typeof(int), logType.GetProperty("Attempts")!.PropertyType);

        Assert.NotNull(logType.GetProperty("ModelName"));
        Assert.Equal(typeof(string), logType.GetProperty("ModelName")!.PropertyType);

        Assert.NotNull(logType.GetProperty("ModelVersion"));
        Assert.Equal(typeof(string), logType.GetProperty("ModelVersion")!.PropertyType);

        Assert.NotNull(logType.GetProperty("ThresholdsJson"));
        Assert.Equal(typeof(string), logType.GetProperty("ThresholdsJson")!.PropertyType);

        // ADR-013-R2 / AC-NF11-12: Log MUST NOT have any image-related fields
        // Verify NO properties containing common image field names
        var propertyNames = allProperties.Select(p => p.Name.ToLowerInvariant()).ToList();

        // Forbidden field name patterns (case-insensitive)
        var forbiddenPatterns = new[]
        {
            "image", "selfie", "photo", "picture", "face", "crop", "embedding", "vector",
            "descriptor", "feature", "biometric", "capture", "scan", "blob", "bytes"
        };

        foreach (var pattern in forbiddenPatterns)
        {
            var matchingProperties = propertyNames
                .Where(name => name.Contains(pattern) && !name.Contains("biometricoutcome"))
                .ToList();

            // Assert: No property name contains forbidden patterns (except "BiometricOutcome" which is the enum result)
            Assert.Empty(matchingProperties);
        }

        // ADR-013-R2: Verify log only stores metadata, not sensitive biometric data
        // Allowed fields: Id, OrgId, BookingId, GuestId, Outcome, Attempts, ModelName, ModelVersion,
        // ThresholdsJson, OperatorUserId, OperatorNote, CreatedAt, and navigation properties
        var expectedFields = new[]
        {
            "id", "orgid", "bookingid", "guestid", "outcome", "attempts", "modelname",
            "modelversion", "thresholdsjson", "operatoruserid", "operatornote", "createdat",
            "org", "booking", "guest", "operatoruser" // navigation properties
        };

        foreach (var propertyName in propertyNames)
        {
            // Assert: Every property is in the expected list
            Assert.Contains(propertyName, expectedFields);
        }

        // ADR-013-R2: The service layer MUST implement:
        // try
        // {
        //     var selfieBytes = await ReadSelfieAsync(...);
        //     var faceEmbedding = await ExtractFaceEmbeddingAsync(selfieBytes);
        //     var similarity = await CompareFacesAsync(documentEmbedding, faceEmbedding);
        //     var outcome = DetermineBiometricOutcome(similarity, thresholds);
        //
        //     await _db.BiometricVerificationLogs.AddAsync(new BiometricVerificationLog
        //     {
        //         Outcome = outcome,
        //         Attempts = attempts,
        //         ModelName = "AuraFace-v1",
        //         ModelVersion = "1.0.0",
        //         ThresholdsJson = JsonSerializer.Serialize(thresholds),
        //         // NO images, NO embeddings
        //     });
        // }
        // finally
        // {
        //     // Delete selfie image, cropped face, embeddings from memory before response
        //     selfieBytes = null;
        //     faceEmbedding = null;
        //     GC.Collect(); // Optional: force immediate cleanup
        // }

        // ADR-013-R3: Verify ITenantOwned for multi-tenancy isolation
        Assert.True(typeof(Core.Multitenancy.ITenantOwned).IsAssignableFrom(logType));
    }
}
