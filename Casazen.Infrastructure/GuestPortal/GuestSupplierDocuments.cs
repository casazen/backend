using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestSupplierDocuments(AppDbContext db, IFileStorage storage, TimeProvider clock, IConfiguration config)
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public sealed record Document(Guid Id, string FileName, DateTime UploadedAt);
    public sealed record OrderDocuments(Guid Id, string ServiceName, string SupplierName, List<Document> Documents);

    public IQueryable<GuestSupplierOrder> PaidOrders => db.GuestSupplierOrders.AsNoTracking()
        .Where(o => o.StripeSandbox && o.StripePaymentIntentId != null && (o.State == GuestSupplierOrderState.Accepted ||
            o.State == GuestSupplierOrderState.Completed || o.State == GuestSupplierOrderState.Disputed ||
            o.State == GuestSupplierOrderState.Resolved || o.State == GuestSupplierOrderState.Released));

    public IQueryable<GuestSupplierOrder> ForStay(Guid orgId, Guid bookingId, Guid propertyId) =>
        PaidOrders.Where(o => o.HostOrgId == orgId && o.BookingId == bookingId && o.PropertyId == propertyId);

    public IQueryable<OrderDocuments> List(IQueryable<GuestSupplierOrder> scope) =>
        scope.OrderByDescending(o => o.RequestedFor).Select(o => new OrderDocuments(o.Id, o.ServiceName, o.SupplierName,
            db.GuestSupplierOrderDocuments.Where(d => d.OrderId == o.Id).OrderByDescending(d => d.UploadedAt).ThenBy(d => d.Id)
                .Select(d => new Document(d.Id, d.FileName, d.UploadedAt)).ToList()));

    public async Task<Document?> UploadAsync(Guid supplierId, Guid orderId, string name, Stream file, CancellationToken ct)
    {
        if (!await PaidOrders.AnyAsync(o => o.Id == orderId && o.SupplierOrgId == supplierId, ct)) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await file.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length < 5 || !buffer.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException();
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        var existing = await db.GuestSupplierOrderDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.OrderId == orderId && d.ContentHash == hash, ct);
        if (existing != null) return View(existing);
        var fileName = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
        fileName = string.Concat(fileName.Where(c => !char.IsControl(c))).Trim();
        if (fileName.Length == 0) fileName = "documento";
        var document = new GuestSupplierOrderDocument
        {
            OrderId = orderId,
            ContentHash = hash,
            FileName = fileName[..Math.Min(fileName.Length, 170)] + ".pdf",
            UploadedAt = clock.GetUtcNow().UtcDateTime
        };
        buffer.Position = 0;
        await storage.PutAsync(StorageBucket.Private, Key(document), buffer, "application/pdf", ct);
        db.GuestSupplierOrderDocuments.Add(document);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_GuestSupplierOrderDocuments_OrderId_ContentHash"
        })
        {
            db.Entry(document).State = EntityState.Detached;
            document = await db.GuestSupplierOrderDocuments.AsNoTracking().SingleAsync(d => d.OrderId == orderId && d.ContentHash == hash, ct);
        }
        return View(document);
    }

    public async Task<(Stream File, string Name)?> OpenAsync(IQueryable<GuestSupplierOrder> scope, Guid documentId, CancellationToken ct)
    {
        var document = await db.GuestSupplierOrderDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId && scope.Any(o => o.Id == d.OrderId), ct);
        if (document == null) return null;
        var file = await storage.OpenReadAsync(StorageBucket.Private, Key(document), ct);
        return file == null ? null : (file, document.FileName);
    }

    public async Task<string?> ReceiptAsync(IQueryable<GuestSupplierOrder> scope, Guid id, CancellationToken ct)
    {
        var order = await scope.SingleOrDefaultAsync(o => o.Id == id, ct);
        var secret = config[$"{GuestSupplierCheckout.Section}:SecretKey"];
        if (order == null || secret?.StartsWith("sk_test_", StringComparison.Ordinal) != true) return null;
        var intent = await new PaymentIntentService(new StripeClient(secret)).GetAsync(order.StripePaymentIntentId!,
            new PaymentIntentGetOptions { Expand = ["latest_charge"] }, cancellationToken: ct);
        if (!GuestSupplierCheckout.Matches(order, intent) || intent.Status != "succeeded" ||
            intent.LatestCharge is not { Livemode: false, Captured: true, Paid: true } charge ||
            charge.PaymentIntentId != intent.Id || !Uri.TryCreate(charge.ReceiptUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Host != "pay.stripe.com" || url.UserInfo.Length != 0) return null;
        return url.AbsoluteUri;
    }

    private static string Key(GuestSupplierOrderDocument document) => $"guest-supplier-orders/{document.OrderId:N}/{document.ContentHash}.pdf";
    private static Document View(GuestSupplierOrderDocument document) => new(document.Id, document.FileName, document.UploadedAt);
}
