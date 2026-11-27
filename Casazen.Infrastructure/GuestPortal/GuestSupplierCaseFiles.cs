using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public enum GuestCaseActor { Guest, Supplier, Host, Staff }

/// <summary>Private case evidence. Raw photos and personal narratives never enter an AI integration.</summary>
public sealed class GuestSupplierCaseFiles(AppDbContext db, IFileStorage storage, TimeProvider clock)
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxPhotos = 3;
    private sealed class CaseFile
    {
        public List<Photo> Photos { get; set; } = [];
        public List<Review> Reviews { get; set; } = [];
        public string? StaffUserId { get; set; }
    }

    public sealed record Photo(Guid Id, string Side, string ContentType, DateTime AddedAt);
    private sealed record Review(Guid Id, string Side, string Request, DateTime RequestedAt, string? Finding = null,
        string? Reviewer = null, DateTime? ClosedAt = null);
    public sealed record ReviewView(Guid Id, string Side, string Request, DateTime RequestedAt, string? Finding, DateTime? ClosedAt);
    public sealed record CaseView(bool Assigned, List<Photo> Photos, List<ReviewView> Reviews);
    public sealed record PhotoContent(Stream Stream, string Type);

    private static string Side(GuestCaseActor actor) => actor.ToString().ToLowerInvariant();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static CaseFile Read(GuestSupplierOrder order) =>
        order.CaseFileJson == null ? new() : JsonSerializer.Deserialize<CaseFile>(order.CaseFileJson)!;
    private static string Key(GuestSupplierOrder order, Photo photo) =>
        $"guest-supplier-cases/{order.HostOrgId:N}/{order.Id:N}/{photo.Side}/{photo.Id:N}";
    private static bool Visible(string side, GuestCaseActor actor) =>
        actor is GuestCaseActor.Staff or GuestCaseActor.Host || Side(actor) == side;
    private static CaseView View(CaseFile file, GuestCaseActor actor) =>
        new(file.StaffUserId != null, file.Photos.Where(p => Visible(p.Side, actor)).ToList(),
            file.Reviews.Where(r => Visible(r.Side, actor)).Select(r =>
                new ReviewView(r.Id, r.Side, r.Request, r.RequestedAt, r.Finding, r.ClosedAt)).ToList());
    public CaseView? Details(GuestSupplierOrder order, GuestCaseActor actor) =>
        order.Complaint == null ? null : View(Read(order), actor);

    public async Task<PhotoContent?> OpenAsync(GuestSupplierOrder order, GuestCaseActor actor, Guid id, CancellationToken ct)
    {
        var photo = Read(order).Photos.SingleOrDefault(p => p.Id == id && Visible(p.Side, actor));
        if (photo == null) return null;
        var stream = await storage.OpenReadAsync(StorageBucket.Private, Key(order, photo), ct);
        return stream == null ? null : new(stream, photo.ContentType);
    }

    public async Task<CaseView?> AddPhotoAsync(GuestSupplierOrder order, GuestCaseActor actor, Guid id, Stream stream, CancellationToken ct)
    {
        if (actor is not (GuestCaseActor.Guest or GuestCaseActor.Supplier) || id == Guid.Empty) return null;
        using var bytes = new MemoryStream();
        var buffer = new byte[32768];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > MaxBytes) throw new InvalidDataException();
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var raw = bytes.ToArray();
        var type = ImageSignature.Detect(raw);
        if (type is not ("image/png" or "image/jpeg" or "image/webp")) throw new InvalidDataException();
        var clean = ShowcaseImageSanitizer.Clean(raw, type) ?? throw new InvalidDataException();
        using var imageStream = new MemoryStream(clean, writable: false);
        var dimensions = await ImageSizeReader.ReadAsync(imageStream, ct);
        if (dimensions is not { } size || size.Width > ShowcaseMediaLimits.MaxSidePixels ||
            size.Height > ShowcaseMediaLimits.MaxSidePixels || (long)size.Width * size.Height > ShowcaseMediaLimits.MaxPixels)
            throw new InvalidDataException();
        return await ChangeAsync(order, actor, async file =>
        {
            if (file.Photos.Any(p => p.Id == id)) return file.Photos.Any(p => p.Id == id && p.Side == Side(actor));
            if (order.Complaint == null || (order.State != GuestSupplierOrderState.Disputed || order.Decision != null) &&
                !file.Reviews.Any(r => r.Side == Side(actor) && r.ClosedAt == null) ||
                file.Photos.Count(p => p.Side == Side(actor)) >= MaxPhotos) return false;
            var photo = new Photo(id, Side(actor), type, Now);
            using var content = new MemoryStream(clean, writable: false);
            await storage.PutAsync(StorageBucket.Private, Key(order, photo), content, type, ct);
            file.Photos.Add(photo);
            return true;
        }, ct);
    }

    public Task<CaseView?> RequestReviewAsync(GuestSupplierOrder order, GuestCaseActor actor, string text, CancellationToken ct) =>
        ChangeAsync(order, actor, file =>
        {
            if (actor is not (GuestCaseActor.Guest or GuestCaseActor.Supplier) || order.HostOrigin || order.Decision == null ||
                order.State is not (GuestSupplierOrderState.Resolved or GuestSupplierOrderState.Released) ||
                string.IsNullOrWhiteSpace(text) || text.Length > 2000) return Task.FromResult(false);
            var existing = file.Reviews.SingleOrDefault(r => r.Side == Side(actor));
            if (existing != null) return Task.FromResult(existing.Request == text.Trim());
            file.Reviews.Add(new(Guid.NewGuid(), Side(actor), text.Trim(), Now));
            return Task.FromResult(true);
        }, ct);

    public Task<CaseView?> ClaimAsync(GuestSupplierOrder order, string user, CancellationToken ct) =>
        ChangeAsync(order, GuestCaseActor.Staff, file =>
        {
            if (order.HostOrigin || order.Complaint == null ||
                order.State != GuestSupplierOrderState.Disputed && !order.HumanReviewPending) return Task.FromResult(false);
            if (file.StaffUserId != null && file.StaffUserId != user) return Task.FromResult(false);
            file.StaffUserId = user;
            return Task.FromResult(true);
        }, ct);

    public Task<CaseView?> CloseReviewAsync(GuestSupplierOrder order, Guid id, string user, string finding, CancellationToken ct) =>
        ChangeAsync(order, GuestCaseActor.Staff, file =>
        {
            var index = file.Reviews.FindIndex(r => r.Id == id);
            if (order.HostOrigin || index < 0 || string.IsNullOrWhiteSpace(finding) || finding.Length > 2000) return Task.FromResult(false);
            var before = file.Reviews[index];
            if (before.ClosedAt != null) return Task.FromResult(before.Reviewer == user && before.Finding == finding.Trim());
            file.Reviews[index] = before with { Finding = finding.Trim(), Reviewer = user, ClosedAt = Now };
            return Task.FromResult(true);
        }, ct);

    private async Task<CaseView?> ChangeAsync(GuestSupplierOrder order, GuestCaseActor actor, Func<CaseFile, Task<bool>> update, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"GuestSupplierOrders\" WHERE \"Id\" = {order.Id} FOR UPDATE", ct);
        await db.Entry(order).ReloadAsync(ct);
        var file = Read(order);
        if (!await update(file)) return null;
        order.CaseFileJson = JsonSerializer.Serialize(file);
        order.HumanReviewPending = file.Reviews.Any(r => r.ClosedAt == null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return View(file, actor);
    }
}
