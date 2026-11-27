using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// What the two import endpoints of the onboarding (the data of the properties, the owner portfolio) do in the same way: who asks, the file and its size, and
/// the bytes read from memory (NF-105, ONB-02, ONB-06). The service that checks and writes the rows, and the DTO of the answer, are the endpoint's.
/// </summary>
internal static class BulkFileImport
{
    /// <summary>
    /// Runs <paramref name="import"/> on the file of the request with the caller's scope: 403 without an org, a scope or a user, 400 without a file, 413 over
    /// <see cref="BulkImportLimits.MaxFileBytes"/> (judged on the declared size, before a byte is copied), otherwise <paramref name="toDto"/> of the result.
    /// </summary>
    public static async Task<ActionResult<TDto>> ImportAsync<TResult, TDto>(
        ControllerBase controller,
        IOrgContextResolver orgContextResolver,
        IHostScopeResolver hostScopeResolver,
        IFormFile? file,
        bool dryRun,
        Func<HostScope, string, string, byte[], bool, CancellationToken, Task<TResult>> import,
        Func<TResult, TDto> toDto,
        CancellationToken cancellationToken)
    {
        var userId = controller.User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(controller.User, orgId.Value, cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return controller.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (file is null || file.Length == 0)
            return controller.ApiProblem(StatusCodes.Status400BadRequest, BulkImportCodes.FileMissing, "OnboardingImportFileMissing");

        // Judged on the size the request declares, before a byte of it is copied.
        if (file.Length > BulkImportLimits.MaxFileBytes)
        {
            return controller.ApiProblem(
                StatusCodes.Status413PayloadTooLarge,
                BulkImportCodes.FileTooLarge,
                "OnboardingImportFileTooLarge",
                BulkImportLimits.MaxFileBytes / (1024 * 1024));
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(content, cancellationToken);

        var result = await import(scope, userId, file.FileName, content, dryRun, cancellationToken);
        return controller.Ok(toDto(result));
    }
}
