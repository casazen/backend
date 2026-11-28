using Casazen.Core.Authorization;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface IConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition against a context (booking, guest profile, etc.).
    /// Respects HostScope perimeter (ADR-014-R4).
    /// </summary>
    /// <param name="condition">Condition tree to evaluate.</param>
    /// <param name="context">Evaluation context (booking data, guest profile, etc.).</param>
    /// <param name="scope">Host scope for perimeter enforcement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if condition matches the context.</returns>
    Task<bool> EvaluateAsync(
        Condition condition,
        object context,
        HostScope scope,
        CancellationToken cancellationToken = default);

    Task<int> PreviewCountAsync(
        Condition condition,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate that a condition is well-formed and uses only allowed fields.
    /// Ensures no Alloggiati/document fields per ADR-014-R5.
    /// </summary>
    /// <param name="condition">Condition tree to validate.</param>
    /// <returns>Validation errors, or empty list if valid.</returns>
    List<string> ValidateCondition(Condition condition);
}
