using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

public static class GuestCancellationProblems
{
    public static ObjectResult RefundChanged(this ControllerBase controller, GuestRefundChangedException exception)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(exception);

        var result = controller.ApiProblem(StatusCodes.Status409Conflict, exception.Code, exception.MessageKey);
        if (result.Value is ProblemDetails problem)
            problem.Extensions["preview"] = GuestCancellationPreviewDto.From(exception.Current);

        return result;
    }

    public static bool IsValidExpectedRefund(decimal? amount) =>
        amount is { } value && value >= 0m && value <= 1_000_000m && decimal.Round(value, 2) == value;
}
