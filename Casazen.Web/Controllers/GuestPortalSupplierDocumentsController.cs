using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalSupplierDocumentsController(GuestSupplierDocuments documents) : GuestPortalControllerBase
{
    [HttpGet("documents/supplier-services")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await documents.List(documents.ForStay(Portal.OrgId, Portal.BookingId, Portal.PropertyId)).ToListAsync(ct));

    [HttpGet("documents/supplier-services/{documentId:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid documentId, CancellationToken ct)
    {
        var file = await documents.OpenAsync(documents.ForStay(Portal.OrgId, Portal.BookingId, Portal.PropertyId), documentId, ct);
        return file is { } result ? File(result.File, "application/pdf", result.Name) : LinkInvalid();
    }

    [HttpGet("documents/supplier-services/{orderId:guid}/payment-receipt")]
    public async Task<IActionResult> Receipt(Guid orderId, CancellationToken ct)
    {
        var url = await documents.ReceiptAsync(documents.ForStay(Portal.OrgId, Portal.BookingId, Portal.PropertyId), orderId, ct);
        return url == null ? NotFound() : Ok(new { url });
    }
}

[ApiController]
[Route("api/supplier/guest-orders/{orderId:guid}/documents")]
[Authorize(Policy = CasazenPolicies.Supplier)]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class SupplierGuestOrderDocumentsController(GuestSupplierDocuments documents, ISupplierOrgContextResolver orgs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid orderId, CancellationToken ct)
    {
        if (await orgs.GetLinkedSupplierOrgIdAsync(ct) is not { } supplier) return NotFound();
        var order = await documents.List(documents.PaidOrders.Where(o => o.SupplierOrgId == supplier && o.Id == orderId)).SingleOrDefaultAsync(ct);
        return order is null ? NotFound() : Ok(order.Documents);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(GuestSupplierDocuments.MaxBytes + 65536)]
    public async Task<IActionResult> Upload(Guid orderId, IFormFile file, CancellationToken ct)
    {
        if (await orgs.GetLinkedSupplierOrgIdAsync(ct) is not { } supplier) return NotFound();
        if (file.Length <= 0 || file.Length > GuestSupplierDocuments.MaxBytes) return BadRequest();
        await using var stream = file.OpenReadStream();
        try
        {
            var document = await documents.UploadAsync(supplier, orderId, file.FileName, stream, ct);
            return document is null ? NotFound() : Ok(document);
        }
        catch (InvalidDataException) { return BadRequest(); }
    }
}
