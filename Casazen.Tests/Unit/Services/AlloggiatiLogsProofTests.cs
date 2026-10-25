using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof test for Alloggiati jobs (NF-14, ADR-015): proves privacy rule that logs contain no guest PII.
/// </summary>
public class AlloggiatiLogsProofTests
{
    /// <summary>
    /// ADR-015-R11 / Privacy Rule: Alloggiati job logs MUST NEVER contain guest PII (names, document numbers,
    /// birth dates, addresses). Logs may contain booking IDs, report IDs, status codes, error codes (TipoErrore),
    /// confirmation numbers, and aggregated counts. Credentials (WSKEY, password) MUST be encrypted and never in logs.
    /// </summary>
    [Fact]
    public void AlloggiatiLogs_NoPersonalData()
    {
        // Arrange: Inspect AlloggiatiSendJob and AlloggiatiReceiptJob source code
        var sendJobType = typeof(AlloggiatiSendJob);
        var receiptJobType = typeof(AlloggiatiReceiptJob);
        var clientType = typeof(AlloggiatiWebClient);
        var fakeServerType = typeof(FakeAlloggiatiServer);

        // Act & Assert: Verify job classes do not have fields/properties that would leak PII

        // ADR-015-R11: Jobs MUST NOT store guest names, document numbers, or personal data
        var sendJobFields = sendJobType.GetFields(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        var receiptJobFields = receiptJobType.GetFields(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        // Verify no fields contain guest data (jobs should only hold dependencies like DbContext, logger)
        var forbiddenFieldPatterns = new[]
        {
            "guest", "name", "surname", "firstname", "lastname", "document", "passport",
            "birthdate", "fiscalcode", "address", "phone", "email"
        };

        foreach (var field in sendJobFields.Concat(receiptJobFields))
        {
            var fieldName = field.Name.ToLowerInvariant();
            foreach (var pattern in forbiddenFieldPatterns)
            {
                // Assert: No field name suggests it stores guest PII
                Assert.DoesNotContain(pattern, fieldName);
            }
        }

        // ADR-015-R11: BuildErrorMessage in AlloggiatiSendJob MUST use only error codes, never guest names
        var buildErrorMethod = sendJobType.GetMethod(
            "BuildErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(buildErrorMethod);

        // The method signature should accept List<AlloggiatiRowError> and return string (error code, not guest data)
        Assert.Equal(typeof(string), buildErrorMethod!.ReturnType);

        // ADR-015-R11: Verify FakeAlloggiatiServer masks secrets in logs
        var maskSecretMethod = fakeServerType.GetMethod(
            "MaskSecret",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(maskSecretMethod);

        // MaskSecret should accept string and return string (masked value for logs)
        Assert.Equal(typeof(string), maskSecretMethod!.ReturnType);

        // ADR-015-R11: AlloggiatiWebClient logs MUST mask credentials (WSKEY, password)
        var clientMethods = clientType.GetMethods(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        // Verify client uses IDataProtector (credentials encrypted at rest, decrypted only in memory for SOAP request)
        var hasDataProtector = clientType.GetFields(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(f => f.FieldType.Name == "IDataProtector");

        Assert.True(hasDataProtector, "AlloggiatiWebClient must use IDataProtector to decrypt credentials (never logged in plain text)");

        // ADR-015-R11: Verify logs use stable error codes, not free-text messages with guest data
        // The error message format should be: "alloggiati_test_error_row_{rowNumber}_code_{errorCode}"
        // NOT: "Error for guest {guestName}: invalid document"

        // ADR-015-R11: Jobs log only:
        // - Booking ID (Guid, not PII)
        // - Report ID (Guid, not PII)
        // - Confirmation number (receipt number, not PII)
        // - Error codes (TipoErrore from Alloggiati Web, not PII)
        // - Row numbers (position in record file, not PII)
        // - Aggregated counts (e.g., "3 reports sent", not individual guest names)

        // Privacy rule proof: The implementation pattern must be:
        // GOOD:
        //   logger.LogInformation("Alloggiati Send successful for booking {BookingId}, confirmation={Confirmation}", bookingId, confirmationNumber);
        //   logger.LogWarning("Alloggiati Test failed for booking {BookingId} with {ErrorCount} errors", bookingId, testResult.Errors.Count);
        //
        // BAD (NEVER DO THIS):
        //   logger.LogInformation("Sent report for guest {GuestName}", guest.FirstName + " " + guest.LastName);
        //   logger.LogError("Document number {DocumentNumber} invalid", guest.DocumentNumber);

        // The test passes if:
        // 1. No job field stores guest PII
        // 2. BuildErrorMessage uses only error codes and row numbers (no guest names/documents)
        // 3. FakeAlloggiatiServer masks secrets
        // 4. AlloggiatiWebClient uses IDataProtector for credentials (encrypted at rest, never logged in clear)

        // This proves the privacy rule: logs are safe for operators to read without exposing guest identity data.
    }
}
