# ADR-036: Photo Public URL Strategy

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-32 (photo management)

## Context

Property photos need public URLs for OTA distribution via Octorate. URLs must be non-guessable per PO-15 requirement. Upload queue enables async processing with progress visibility.

**Existing code:**
- `PropertyImage` entity exists
- No public URL generation
- No upload queue

**Repo scope:** Backend (URL generation, upload queue, EXIF stripping), Frontend (photo upload UI, room assignment), Mobile (none - photo management is desktop)

## Decision

### UUID-Based URLs

Per Q-A17: database table for queue, UUID for non-guessable URLs.

```csharp
public class PropertyImage
{
    // ... existing fields ...
    public Guid PublicId { get; set; }                  // UUID v4 for URL
    public string? RoomType { get; set; }               // Per Q-A18: stored in CasaZen
    public ImageUploadStatus UploadStatus { get; set; }
}

public enum ImageUploadStatus { Pending, Uploading, Completed, Failed }
```

Public URL format: `/photos/public/{uuid}.jpg`

### Upload Queue

```csharp
public class PhotoUploadTask : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid PropertyImageId { get; set; }
    public string LocalFilePath { get; set; }
    public string? TargetRoomType { get; set; }
    public PhotoTaskStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum PhotoTaskStatus { Pending, Processing, Completed, Failed }
```

### EXIF Stripping

```csharp
public async Task<byte[]> ProcessImageAsync(Stream input, CancellationToken ct)
{
    using var image = await Image.LoadAsync(input, ct);
    
    // Strip EXIF (geolocation, camera info)
    image.Metadata.ExifProfile = null;
    
    // Resize if needed
    if (image.Width > 2048 || image.Height > 2048)
    {
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(2048, 2048),
            Mode = ResizeMode.Max
        }));
    }
    
    using var output = new MemoryStream();
    await image.SaveAsJpegAsync(output, ct);
    return output.ToArray();
}
```

Uses `SixLabors.ImageSharp` (already in codebase for invoice logos).

### Upload Processing Job

```csharp
public class PhotoUploadJob
{
    public async Task ProcessAsync(Guid taskId, CancellationToken ct)
    {
        var task = await _db.PhotoUploadTasks.FindAsync(taskId, ct);
        task.Status = PhotoTaskStatus.Processing;
        await _db.SaveChangesAsync(ct);
        
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(task.LocalFilePath, ct);
            var processed = await _imageService.ProcessImageAsync(new MemoryStream(fileBytes), ct);
            
            var image = await _db.PropertyImages.FindAsync(task.PropertyImageId, ct);
            var publicId = Guid.NewGuid();
            
            await _storage.UploadAsync($"photos/{publicId}.jpg", processed, ct);
            
            image.PublicId = publicId;
            image.RoomType = task.TargetRoomType;
            image.UploadStatus = ImageUploadStatus.Completed;
            
            task.Status = PhotoTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            
            // Sync to Octorate
            await _octorate.UploadPhotoAsync(image.PropertyId, $"/photos/public/{publicId}.jpg", ct);
        }
        catch (Exception ex)
        {
            task.Status = PhotoTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            
            var image = await _db.PropertyImages.FindAsync(task.PropertyImageId, ct);
            image.UploadStatus = ImageUploadStatus.Failed;
        }
        
        await _db.SaveChangesAsync(ct);
    }
}
```

### Rate Limiting

Public photo endpoint rate-limited to prevent brute-force:

```csharp
[EnableRateLimiting("public-photos")]
[HttpGet("/photos/public/{uuid}.jpg")]
public async Task<IActionResult> GetPhoto(Guid uuid)
{
    // 100 req/min per IP
}
```

### Rejected Alternatives

- **Presigned URLs (ADR-007 HMAC):** UUID v4 has 128-bit entropy; sufficient for non-guessable
- **Store in Octorate only:** Need local copy for CasaZen UI and fallback
- **Sequential IDs:** Guessable; UUID required per PO-15

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-036-R1 | P0 | `PropertyImage.PublicId` Guid MUST be cryptographically secure UUID v4 (`Guid.NewGuid()` in .NET); URL format `/photos/public/{uuid}.jpg` |
| ADR-036-R2 | P0 | Photo upload MUST strip EXIF metadata (geolocation, camera info) using `ImageSharp` `image.Metadata.ExifProfile = null` |
| ADR-036-R3 | P0 | `PhotoUploadTask` table (OrgId, PropertyId, PropertyImageId, LocalFilePath, TargetRoomType nullable, Status enum, CreatedAt, CompletedAt nullable, ErrorMessage nullable, ITenantOwned) |
| ADR-036-R4 | P0 | Photo upload endpoint `POST /api/properties/{id}/photos` MUST create `PhotoUploadTask` and return immediately; processing via Hangfire job |
| ADR-036-R5 | P0 | `PropertyImage.RoomType` varchar(50) nullable MUST store room assignment; frontend photo gallery groups by RoomType |
| ADR-036-R6 | P0 | Public photo endpoint `GET /photos/public/{uuid}.jpg` MUST rate limit 100 req/min per IP (brute-force protection) |
| ADR-036-R7 | P1 | Upload progress: `GET /api/properties/{id}/photos/tasks` returns pending/processing/completed tasks with status |
| ADR-036-R8 | P1 | Photo upload task retention: 90 days after completion; `PhotoUploadTaskCleanupJob` (Hangfire weekly) deletes old completed tasks |

## Consequences

**Follow-on work:**
- Backend: `PhotoUploadTask` entity, upload job, EXIF stripping, public URL endpoint with rate limiting
- Frontend: Photo upload with queue display, room assignment dropdown, progress indicator
- Mobile: None (photo management is desktop)
- Tests: EXIF stripping proof test, rate limiting integration test

**Tier 2 priority:** This is a Tier 2 ADR. May be deferred if wave 1 timeline is tight.

**Risks:**
- UUID collision probability negligible (2^-64 for 1 billion photos)
- Public URL never expires: if photo deleted, Octorate shows 404 (acceptable)

**CI cost:** +5s for EXIF stripping test
