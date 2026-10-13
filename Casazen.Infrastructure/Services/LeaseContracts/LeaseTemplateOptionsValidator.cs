using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.LeaseContracts;

/// <summary>
/// Startup check of the lease contract template approvals (LT-03, A7-03). Outside Development and Testing (both
/// Railway environments run as Production) a variant declared <c>Approved=true</c> stops the startup when the approval
/// is not real: <c>dev-stub</c> or empty version, no reference or date, or a template file missing or incomplete.
/// In Development and Testing the same variant is only treated as not approved (the API answers 422).
/// </summary>
public sealed class LeaseTemplateOptionsValidator(IHostEnvironment environment) : IValidateOptions<LeaseTemplateOptions>
{
    public const string Runbook = "docs/runbooks/lease-contract-templates.md";

    public ValidateOptionsResult Validate(string? name, LeaseTemplateOptions options)
    {
        if (!RequiresValidApprovals(environment))
            return ValidateOptionsResult.Success;

        var failures = GetFailures(options, environment.ContentRootPath);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static bool RequiresValidApprovals(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");

    public static IReadOnlyList<string> GetFailures(LeaseTemplateOptions options, string contentRootPath)
    {
        var failures = new List<string>();
        foreach (var (key, variant) in options.Variants)
        {
            if (variant is null || !variant.Approved)
                continue;

            var prefix = $"LeaseTemplates:Variants:{key}";
            var regimeName = Enum.GetNames<FiscalRegime>()
                .FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
            if (regimeName is null)
            {
                failures.Add($"{prefix}: '{key}' is not a fiscal regime ({string.Join(", ", Enum.GetNames<FiscalRegime>())}). See {Runbook}.");
                continue;
            }

            var reasons = LeaseTemplateApproval.GetInvalidApprovalReasons(variant);
            if (reasons.Count > 0)
            {
                failures.AddRange(reasons.Select(r => $"{prefix}: Approved=true but {r}. See {Runbook}."));
                continue;
            }

            var state = LeaseContractTemplateCatalog.Load(Enum.Parse<FiscalRegime>(regimeName), options, contentRootPath);
            if (!state.IsApproved)
            {
                failures.Add(
                    $"{prefix}: Approved=true but the template is {state.Status}: {string.Join("; ", state.Problems)}. See {Runbook}.");
            }
        }

        return failures;
    }
}
