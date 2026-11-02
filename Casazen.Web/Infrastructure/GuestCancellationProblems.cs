using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>The errors of the cancellation of the guest that carry more than a code (DB-08a).</summary>
public static class GuestCancellationProblems
{
    /// <summary>
    /// 409 <c>refund_changed</c>: the refund is not the one the guest was shown and nothing was cancelled. The problem carries the
    /// <c>preview</c> as it is now, so the screen shows the new amount and asks again without another call.
    /// </summary>
    public static ObjectResult RefundChanged(this ControllerBase controller, GuestRefundChangedException exception)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(exception);

        var result = controller.ApiProblem(StatusCodes.Status409Conflict, exception.Code, exception.MessageKey);
        if (result.Value is ProblemDetails problem)
            problem.Extensions["preview"] = GuestCancellationPreviewDto.From(exception.Current);

        return result;
    }

    /// <summary>True when <paramref name="amount"/> is a non-negative amount of euros with at most two decimals (else the answer is 422 <c>refund_amount_invalid</c>).</summary>
    public static bool IsValidExpectedRefund(decimal? amount) =>
        amount is { } value && value >= 0m && value <= 1_000_000m && decimal.Round(value, 2) == value;
}
