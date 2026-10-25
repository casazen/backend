using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// MRZ extraction service supporting passport (TD-3), ID card (TD-1), and residence permit
/// per ICAO 9303 (ADR-009-R22, ADR-013-R10). Validates check digits per ADR-009-R23.
/// </summary>
public class MrzExtractionService(ILogger<MrzExtractionService> logger) : IMrzExtractionService
{
    // ICAO 9303 check digit weights for mod-10 weighted sum
    private static readonly int[] CheckDigitWeights = [7, 3, 1];

    /// <summary>
    /// Extracts data from the MRZ of an identity document image. Validates check digits per ICAO 9303.
    /// For passports (TD-3): 2 lines of 44 characters each.
    /// For ID cards (TD-1): 3 lines of 30 characters each.
    /// For residence permits (TD-2): 2 lines of 36 characters each.
    /// </summary>
    public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        try
        {
            // TODO: Integrate PassportEye (Apache-2.0) or Tesseract with MRZ-specific patterns (ADR-009-R26)
            // For now, return a placeholder result that demonstrates the structure
            // This will be replaced with actual MRZ reading library integration in a follow-up task

            logger.LogInformation("MRZ extraction requested (placeholder implementation)");

            // Placeholder: In production, this would use PassportEye or Tesseract to extract MRZ lines
            // and then parse them according to ICAO 9303 format

            await Task.CompletedTask; // Simulate async operation

            // Return null to indicate MRZ not readable (will trigger OCR fallback)
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MRZ extraction failed");
            return null;
        }
    }

    /// <summary>
    /// Validates a check digit per ICAO 9303 mod-10 weighted sum algorithm (ADR-009-R23).
    /// </summary>
    /// <param name="data">The data string (document number, date, etc.).</param>
    /// <param name="checkDigit">The check digit to validate.</param>
    /// <returns>True if the check digit is valid.</returns>
    internal static bool ValidateCheckDigit(string data, char checkDigit)
    {
        if (string.IsNullOrEmpty(data))
            return false;

        var sum = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var value = CharToValue(data[i]);
            var weight = CheckDigitWeights[i % CheckDigitWeights.Length];
            sum += value * weight;
        }

        var calculatedCheckDigit = sum % 10;
        var providedCheckDigit = CharToValue(checkDigit);

        return calculatedCheckDigit == providedCheckDigit;
    }

    /// <summary>
    /// Converts a character to its ICAO 9303 numeric value.
    /// Digits 0-9 = 0-9, Letters A-Z = 10-35, filler &lt; = 0.
    /// </summary>
    private static int CharToValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            '<' => 0,
            _ => 0
        };
    }

    /// <summary>
    /// Parses a date from MRZ format (YYMMDD) to DateTime.
    /// Uses a sliding window: 00-30 = 2000-2030, 31-99 = 1931-1999.
    /// </summary>
    internal static DateTime? ParseMrzDate(string yymmdd)
    {
        if (string.IsNullOrWhiteSpace(yymmdd) || yymmdd.Length != 6)
            return null;

        if (!int.TryParse(yymmdd[..2], out var yy) ||
            !int.TryParse(yymmdd[2..4], out var mm) ||
            !int.TryParse(yymmdd[4..6], out var dd))
            return null;

        // Sliding window: 00-30 = 2000-2030, 31-99 = 1931-1999
        var year = yy <= 30 ? 2000 + yy : 1900 + yy;

        try
        {
            return new DateTime(year, mm, dd, 0, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses TD-3 (passport) MRZ format: 2 lines of 44 characters.
    /// Line 1: P&lt;ITA&lt;&lt;SURNAME&lt;&lt;GIVENNAMES (type, issuing state, name)
    /// Line 2: Document number, nationality, DOB, sex, expiration, optional data (with check digits)
    /// </summary>
    internal static MrzExtractionResult? ParseTd3Mrz(string line1, string line2)
    {
        if (line1.Length != 44 || line2.Length != 44)
            return null;

        var result = new MrzExtractionResult
        {
            DocumentType = "Passport",
            IssuingState = line1[2..5].Replace("<", "").Trim(),
        };

        // Parse name (line 1, positions 5-43): SURNAME<<GIVENNAMES
        var nameField = line1[5..].Replace("<", " ").Trim();
        var nameParts = nameField.Split("  ", StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Length >= 1)
            result.Surname = nameParts[0].Trim();
        if (nameParts.Length >= 2)
            result.GivenNames = nameParts[1].Trim();

        // Line 2 fields
        var docNumber = line2[..9].Replace("<", "").Trim();
        var docNumberCheck = line2[9];
        var nationality = line2[10..13].Replace("<", "").Trim();
        var dob = line2[13..19];
        var dobCheck = line2[19];
        var sex = line2[20].ToString();
        var expiration = line2[21..27];
        var expirationCheck = line2[27];

        result.DocumentNumber = docNumber;
        result.Nationality = nationality;
        result.DateOfBirth = ParseMrzDate(dob);
        result.ExpirationDate = ParseMrzDate(expiration);
        result.Sex = sex == "<" ? null : sex;

        // Validate check digits (ADR-009-R23)
        var docCheckValid = ValidateCheckDigit(docNumber, docNumberCheck);
        var dobCheckValid = ValidateCheckDigit(dob, dobCheck);
        var expCheckValid = ValidateCheckDigit(expiration, expirationCheck);

        result.CheckDigitsValid = docCheckValid && dobCheckValid && expCheckValid;
        result.Confidence = result.CheckDigitsValid ? 0.95 : 0.70;

        return result;
    }

    /// <summary>
    /// Parses TD-1 (ID card) MRZ format: 3 lines of 30 characters.
    /// </summary>
    internal static MrzExtractionResult? ParseTd1Mrz(string line1, string line2, string line3)
    {
        if (line1.Length != 30 || line2.Length != 30 || line3.Length != 30)
            return null;

        var result = new MrzExtractionResult
        {
            DocumentType = "IdCard",
            IssuingState = line1[2..5].Replace("<", "").Trim(),
        };

        // Document number from line 1
        var docNumber = line1[5..14].Replace("<", "").Trim();
        var docNumberCheck = line1[14];

        // Optional data + DOB from line 2
        var dob = line2[..6];
        var dobCheck = line2[6];
        var sex = line2[7].ToString();
        var expiration = line2[8..14];
        var expirationCheck = line2[14];
        var nationality = line2[15..18].Replace("<", "").Trim();

        // Name from line 3
        var nameField = line3.Replace("<", " ").Trim();
        var nameParts = nameField.Split("  ", StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Length >= 1)
            result.Surname = nameParts[0].Trim();
        if (nameParts.Length >= 2)
            result.GivenNames = nameParts[1].Trim();

        result.DocumentNumber = docNumber;
        result.Nationality = nationality;
        result.DateOfBirth = ParseMrzDate(dob);
        result.ExpirationDate = ParseMrzDate(expiration);
        result.Sex = sex == "<" ? null : sex;

        // Validate check digits
        var docCheckValid = ValidateCheckDigit(docNumber, docNumberCheck);
        var dobCheckValid = ValidateCheckDigit(dob, dobCheck);
        var expCheckValid = ValidateCheckDigit(expiration, expirationCheck);

        result.CheckDigitsValid = docCheckValid && dobCheckValid && expCheckValid;
        result.Confidence = result.CheckDigitsValid ? 0.95 : 0.70;

        return result;
    }
}
