using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

public sealed class ProblemDetailsResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: ProblemDetails problem } result)
            return;

        problem.Status ??= result.StatusCode;
        ApiProblemDetails.Complete(problem, context.HttpContext);
        result.StatusCode ??= problem.Status;
        if (result.ContentTypes.Count == 0)
            result.ContentTypes.Add(ApiProblemDetails.ContentType);
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
