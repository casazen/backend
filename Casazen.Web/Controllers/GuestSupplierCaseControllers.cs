using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed record CaseStatement([Required, MaxLength(2000)] string Text);

public sealed partial class GuestPortalSupplierOrdersController
{
    [HttpGet("supplier-orders/{id:guid}/case")]
    public Task<IActionResult> CaseDetails(Guid id, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => Task.FromResult<IActionResult>(files.Details(order, GuestCaseActor.Guest) is { } view ? Ok(view) : NotFound()), ct);

    [HttpGet("supplier-orders/{id:guid}/case/photos/{photoId:guid}")]
    public Task<IActionResult> OpenCasePhoto(Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => CasePhotoResults.OpenAsync(this, files, order, GuestCaseActor.Guest, photoId, ct), ct);

    [HttpPut("supplier-orders/{id:guid}/case/photos/{photoId:guid}")]
    [RequestSizeLimit(GuestSupplierCaseFiles.MaxBytes)]
    public Task<IActionResult> UploadCasePhoto(Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => CasePhotoResults.UploadAsync(this, files, order, GuestCaseActor.Guest, photoId, ct), ct);

    [HttpPost("supplier-orders/{id:guid}/case/reviews")]
    public Task<IActionResult> RequestCaseReview(Guid id, CaseStatement request, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, async order => await files.RequestReviewAsync(order, GuestCaseActor.Guest, request.Text, ct) is { } view ? Ok(view) : Conflict(), ct);

    private async Task<IActionResult> WithCaseAsync(Guid id, Func<GuestSupplierOrder, Task<IActionResult>> action, CancellationToken ct)
    {
        if (!Sandbox) return NotFound();
        var order = await StayOrders().SingleOrDefaultAsync(x => x.Id == id, ct);
        return order?.Complaint == null ? NotFound() : await action(order);
    }
}

public sealed partial class HostGuestSupplierOrdersController
{
    [HttpGet("{id:guid}/case")]
    public Task<IActionResult> CaseDetails(Guid propertyId, Guid id, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(propertyId, id, order => Task.FromResult<IActionResult>(files.Details(order, GuestCaseActor.Host) is { } view ? Ok(view) : NotFound()), ct);

    [HttpGet("{id:guid}/case/photos/{photoId:guid}")]
    public Task<IActionResult> OpenCasePhoto(Guid propertyId, Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(propertyId, id, order => CasePhotoResults.OpenAsync(this, files, order, GuestCaseActor.Host, photoId, ct), ct);

    private async Task<IActionResult> WithCaseAsync(Guid propertyId, Guid id, Func<GuestSupplierOrder, Task<IActionResult>> action, CancellationToken ct)
    {
        if (!GuestSupplierCheckout.OrdersEnabled(configuration)) return NotFound();
        var (org, failure) = await ScopeAsync(propertyId, ct);
        if (failure != null) return failure;
        var order = await Orders(org, propertyId).SingleOrDefaultAsync(x => x.Id == id && x.HostOrigin, ct);
        return order?.Complaint == null ? NotFound() : await action(order);
    }
}

public sealed partial class SupplierGuestOrdersController
{
    [HttpGet("{id:guid}/case")]
    public Task<IActionResult> CaseDetails(Guid id, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => Task.FromResult<IActionResult>(files.Details(order, GuestCaseActor.Supplier) is { } view ? Ok(view) : NotFound()), ct);

    [HttpGet("{id:guid}/case/photos/{photoId:guid}")]
    public Task<IActionResult> OpenCasePhoto(Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => CasePhotoResults.OpenAsync(this, files, order, GuestCaseActor.Supplier, photoId, ct), ct);

    [HttpPut("{id:guid}/case/photos/{photoId:guid}")]
    [RequestSizeLimit(GuestSupplierCaseFiles.MaxBytes)]
    public Task<IActionResult> UploadCasePhoto(Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => CasePhotoResults.UploadAsync(this, files, order, GuestCaseActor.Supplier, photoId, ct), ct);

    [HttpPost("{id:guid}/case/reviews")]
    public Task<IActionResult> RequestCaseReview(Guid id, CaseStatement request, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, async order => await files.RequestReviewAsync(order, GuestCaseActor.Supplier, request.Text, ct) is { } view ? Ok(view) : Conflict(), ct);

    private async Task<IActionResult> WithCaseAsync(Guid id, Func<GuestSupplierOrder, Task<IActionResult>> action, CancellationToken ct)
    {
        var org = await SupplierAsync(ct);
        if (org == null) return NotFound();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && x.SupplierOrgId == org, ct);
        return order?.Complaint == null ? NotFound() : await action(order);
    }
}

public sealed partial class AdminGuestSupplierOrdersController
{
    [HttpGet("{id:guid}/case")]
    public Task<IActionResult> CaseDetails(Guid id, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => Task.FromResult<IActionResult>(files.Details(order, GuestCaseActor.Staff) is { } view ? Ok(view) : NotFound()), ct);

    [HttpGet("{id:guid}/case/photos/{photoId:guid}")]
    public Task<IActionResult> OpenCasePhoto(Guid id, Guid photoId, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, order => CasePhotoResults.OpenAsync(this, files, order, GuestCaseActor.Staff, photoId, ct), ct);

    [HttpPost("{id:guid}/case/claim")]
    public Task<IActionResult> ClaimCase(Guid id, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, async order => User.GetUserId() is { } user && await files.ClaimAsync(order, user, ct) is { } view ? Ok(view) : Conflict(), ct);

    [HttpPost("{id:guid}/case/reviews/{reviewId:guid}/close")]
    public Task<IActionResult> CloseCaseReview(Guid id, Guid reviewId, CaseStatement request, [FromServices] GuestSupplierCaseFiles files, CancellationToken ct) =>
        WithCaseAsync(id, async order => User.GetUserId() is { } user && await files.CloseReviewAsync(order, reviewId, user, request.Text, ct) is { } view ? Ok(view) : Conflict(), ct);

    private async Task<IActionResult> WithCaseAsync(Guid id, Func<GuestSupplierOrder, Task<IActionResult>> action, CancellationToken ct)
    {
        if (!GuestSupplierCheckout.OrdersEnabled(configuration)) return NotFound();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && !x.HostOrigin, ct);
        return order?.Complaint == null ? NotFound() : await action(order);
    }
}

internal static class CasePhotoResults
{
    public static async Task<IActionResult> OpenAsync(ControllerBase api, GuestSupplierCaseFiles files, GuestSupplierOrder order,
        GuestCaseActor actor, Guid photoId, CancellationToken ct)
    {
        var photo = await files.OpenAsync(order, actor, photoId, ct);
        if (photo == null) return api.NotFound();
        api.Response.Headers.CacheControl = "no-store";
        api.Response.Headers.XContentTypeOptions = "nosniff";
        return api.File(photo.Stream, photo.Type);
    }

    public static async Task<IActionResult> UploadAsync(ControllerBase api, GuestSupplierCaseFiles files, GuestSupplierOrder order,
        GuestCaseActor actor, Guid photoId, CancellationToken ct)
    {
        try
        {
            var view = await files.AddPhotoAsync(order, actor, photoId, api.Request.Body, ct);
            return view == null ? api.Conflict() : api.Ok(view);
        }
        catch (InvalidDataException) { return api.BadRequest(); }
    }
}
