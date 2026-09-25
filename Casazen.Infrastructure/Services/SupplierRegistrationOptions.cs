using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier self-serve registration (section <c>Suppliers</c>, SU-01 / A4-04). The spec allows self-serve signup only
/// "for pilot comune" (<c>spec-supplier-console-web</c> AC3) and names none, so the pilot comuni are configuration with
/// no default: while the list is empty self-serve registration is off and suppliers join by admin invite only.
/// Runbook <c>docs/runbooks/suppliers.md</c>.
/// </summary>
public sealed class SupplierRegistrationOptions
{
    public const string SectionName = "Suppliers";

    /// <summary>
    /// Comuni where a supplier may register without an invite (<c>Suppliers__PilotComuni__0__Code</c>,
    /// <c>Suppliers__PilotComuni__0__Name</c>, …). The code is compared with the registration's comune code as written
    /// (trimmed, case-insensitive) and stored on the supplier profile.
    /// </summary>
    public List<SupplierPilotComune> PilotComuni { get; set; } = [];

    public bool SelfServeEnabled => PilotComuni.Count > 0;

    /// <summary>The pilot comune with this code, or null.</summary>
    public SupplierPilotComune? FindPilotComune(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : PilotComuni.FirstOrDefault(c => string.Equals(c.Code.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A pilot comune: its code (the format used by admin invites) and the name shown to suppliers.</summary>
public sealed class SupplierPilotComune
{
    /// <summary>Maximum length of a comune code (same as <c>SupplierInviteRecord.ComuneCode</c>).</summary>
    public const int CodeMaxLength = 20;

    public const int NameMaxLength = 100;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Rejects incomplete or duplicated pilot comuni at startup, so a typo never silently opens or closes a comune.</summary>
public sealed class SupplierRegistrationOptionsValidator : IValidateOptions<SupplierRegistrationOptions>
{
    public ValidateOptionsResult Validate(string? name, SupplierRegistrationOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.PilotComuni.Count; i++)
        {
            var comune = options.PilotComuni[i];
            var code = comune.Code?.Trim() ?? string.Empty;
            var comuneName = comune.Name?.Trim() ?? string.Empty;

            if (code.Length is 0 or > SupplierPilotComune.CodeMaxLength)
                failures.Add($"Suppliers__PilotComuni__{i}__Code is missing or longer than {SupplierPilotComune.CodeMaxLength} characters.");
            else if (!seen.Add(code))
                failures.Add($"Suppliers__PilotComuni__{i}__Code repeats the code of a previous pilot comune.");

            if (comuneName.Length is 0 or > SupplierPilotComune.NameMaxLength)
                failures.Add($"Suppliers__PilotComuni__{i}__Name is missing or longer than {SupplierPilotComune.NameMaxLength} characters.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
