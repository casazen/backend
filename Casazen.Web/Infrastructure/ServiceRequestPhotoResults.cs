using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The answer of the endpoints that serve a photo of the work of a service request (SP-04), shared by the short-rent and the
/// long-rent controller: the caller has authorized the request, this opens its photo from the private bucket.
/// </summary>
internal static class ServiceRequestPhotoResults
{
    /// <summary>
    /// The file of the photo: never cached (it is a private file, read only here). 404 <c>service_request_not_found</c> when
    /// <paramref name="request"/> is null (outside the caller's scope), 404 <c>service_request_photo_not_found</c> when the request
    /// has no such photo or the file is gone.
    /// </summary>
    public static async Task<IActionResult> ForAsync(
        ControllerBase controller,
        IServiceRequestService serviceRequests,
        ServiceRequest? request,
        Guid photoId,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return ServiceRequestsController.ServiceRequestNotFound(controller);

        var photo = await serviceRequests.OpenWorkPhotoAsync(request, photoId, cancellationToken);
        if (photo is null)
        {
            return controller.ApiProblem(
                StatusCodes.Status404NotFound,
                ServiceRequestErrorCodes.PhotoNotFound,
                ServiceRequestErrorCodes.PhotoNotFoundMessageKey);
        }

        controller.Response.Headers.CacheControl = "private, no-store";
        return controller.File(photo.Content, photo.ContentType, photo.FileName);
    }
}
