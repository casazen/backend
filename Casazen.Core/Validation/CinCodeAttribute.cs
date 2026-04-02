using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Casazen.Core.Validation;

/// <summary>
/// Validates CIN (Codice Identificativo Nazionale) format per D.L. 145/2023.
/// Format: IT-XXXXX-XXXXXXXXXX (IT-region code-unique number)
/// </summary>
public class CinCodeAttribute : ValidationAttribute
{
    private const string CinPattern = @"^IT-\d{5}-\d{10}$";

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // CIN code is optional, so null or empty is valid
        if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
        {
            return ValidationResult.Success;
        }

        var cinCode = value.ToString()!;

        if (!Regex.IsMatch(cinCode, CinPattern))
        {
            return new ValidationResult(
                $"CIN code must match format IT-XXXXX-XXXXXXXXXX (e.g., IT-12345-0123456789). " +
                $"Italian regulatory requirement per D.L. 145/2023.");
        }

        return ValidationResult.Success;
    }
}
