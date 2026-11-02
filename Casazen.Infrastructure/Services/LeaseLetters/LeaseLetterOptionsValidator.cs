using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.LeaseLetters;

/// <summary>
/// Startup check of the approvals of the letters (LR-OPS-1), the same rule as the contract templates (<see cref="LeaseTemplateOptionsValidator"/>):
/// outside Development and Testing (both Railway environments run as Production) a letter declared <c>Approved=true</c> stops the startup
/// when the approval is not real: no version or the <c>dev-stub</c> one, no reference or date, a name that is not a letter, or a file that is
/// missing or incomplete. Railway keeps the previous deployment. In Development and Testing the same letter only counts as not approved.
/// A letter that is not declared approved never stops anything: it is printed as a draft.
/// </summary>
public sealed class LeaseLetterOptionsValidator(IHostEnvironment environment) : IValidateOptions<LeaseLetterOptions>
{
    public const string Runbook = "docs/runbooks/lease-operations.md";

    public ValidateOptionsResult Validate(string? name, LeaseLetterOptions options)
    {
        if (!LeaseTemplateOptionsValidator.RequiresValidApprovals(environment))
            return ValidateOptionsResult.Success;

        var failures = GetFailures(options, environment.ContentRootPath);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> GetFailures(LeaseLetterOptions options, string contentRootPath)
    {
        var failures = new List<string>();
        foreach (var (key, variant) in options.Variants)
        {
            if (variant is null || !variant.Approved)
                continue;

            var prefix = $"LeaseLetters:Variants:{key}";
            var kindName = Enum.GetNames<LeaseLetterKind>()
                .FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
            if (kindName is null)
            {
                failures.Add($"{prefix}: '{key}' is not a letter ({string.Join(", ", Enum.GetNames<LeaseLetterKind>())}). See {Runbook}.");
                continue;
            }

            var reasons = LeaseTemplateApproval.GetInvalidApprovalReasons(variant);
            if (reasons.Count > 0)
            {
                failures.AddRange(reasons.Select(r => $"{prefix}: Approved=true but {r}. See {Runbook}."));
                continue;
            }

            var state = LeaseLetterCatalog.Load(Enum.Parse<LeaseLetterKind>(kindName), options, contentRootPath);
            if (!state.IsApproved)
            {
                failures.Add(
                    $"{prefix}: Approved=true but the letter is {state.Status}: {string.Join("; ", state.Problems)}. See {Runbook}.");
            }
        }

        return failures;
    }
}
