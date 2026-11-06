using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Photos;

/// <summary>
/// The photos of a listing on the channel manager (E02/F03/US01/T02 and T07, NF-32, ADR-036-R2, R3): one <see cref="PhotoUploadTask"/> for each
/// public photo, sent once by its address, followed in the queue of the channel manager until it has the photo, and removed when the photo leaves the
/// gallery. A photo is its address (there is no photo id in CasaZen), so the unique index on the property and the address is what keeps one photo from
/// being sent twice, and a task that is done, or waiting in the queue, is never sent again.
/// </summary>
/// <remarks>
/// <para>The caller opens <see cref="IOctorateOrgScope.Use"/> first (the client takes no org). Every task is saved as soon as it changes, so a failure
/// half way leaves the others as they were. A failure of the channel manager other than a refusal of one photo (it answered 5xx, the rate limit of the
/// org) is not caught here: the caller records it.</para>
/// <para><b>Deleting in CasaZen removes the photo on the channels.</b> A task whose address is no longer in the gallery becomes
/// <see cref="PhotoUploadStatus.RemovePending"/> (the deletion of a photo marks it at once; this is the net for any other way a photo leaves) and
/// the next run deletes the photo on the channel manager. The decision is taken on the whole gallery as stored, not on the photos that pass the test of
/// <see cref="PublicPhotoUrlService"/>, so a storage setting that stops an address from passing can never remove photos.</para>
/// </remarks>
public sealed class ListingPhotoSync(
    AppDbContext db,
    IOctorateApiClient client,
    PublicPhotoUrlService publicUrls,
    TimeProvider timeProvider,
    ILogger<ListingPhotoSync> logger)
{
    /// <summary>Sends the photos not sent yet, and removes the ones that left the gallery.</summary>
    public async Task SendAsync(Property property, string accommodationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(property);

        var gallery = publicUrls.ListingPhotoUrls(property);
        var stored = property.PhotoUrls.ToHashSet(StringComparer.Ordinal);
        var tasks = await db.PhotoUploadTasks.Where(t => t.PropertyId == property.Id).ToListAsync(ct);
        var now = Now();

        foreach (var url in gallery.Where(url => tasks.All(t => !string.Equals(t.PhotoUrl, url, StringComparison.Ordinal))))
        {
            var task = new PhotoUploadTask { OrgId = property.OrgId, PropertyId = property.Id, PhotoUrl = url, CreatedAt = now };
            db.PhotoUploadTasks.Add(task);
            tasks.Add(task);
        }

        foreach (var task in tasks.Where(t => !stored.Contains(t.PhotoUrl) && t.Status is PhotoUploadStatus.Pending or PhotoUploadStatus.Uploading or PhotoUploadStatus.Done or PhotoUploadStatus.Error))
            task.Status = PhotoUploadStatus.RemovePending;

        await db.SaveChangesAsync(ct);

        foreach (var task in tasks.Where(t => t.Status is PhotoUploadStatus.Pending or PhotoUploadStatus.Error && gallery.Contains(t.PhotoUrl, StringComparer.Ordinal)))
        {
            try
            {
                Apply(task, await client.UploadPhotoAsync(accommodationId, task.PhotoUrl, ct));
            }
            catch (OctorateRateLimitException)
            {
                // The org's calls for the minute are spent: the photos left stay pending for the next run.
                logger.LogWarning("Photos of property {PropertyId}: the rate limit of the org stopped the sending, the rest stays pending", property.Id);
                break;
            }
            catch (OctorateRequestRejectedException ex)
            {
                task.Status = PhotoUploadStatus.Error;
                task.ErrorMessage = $"The channel manager refused the photo (HTTP {ex.OctorateStatusCode}).";
                task.CompletedAt = Now();
            }

            await db.SaveChangesAsync(ct);
        }

        await RemoveAsync(tasks.Where(t => t.Status == PhotoUploadStatus.RemovePending), accommodationId, ct);
    }

    /// <summary>
    /// Reads the queue of the channel manager for the photos still waiting (<see cref="PhotoUploadStatus.Uploading"/>): <c>Done</c> with the name
    /// Octorate gave the photo, or <c>Error</c> with the reason; and removes the photos that left the gallery. Returns how many tasks changed.
    /// </summary>
    public async Task<int> ReadQueueAsync(IReadOnlyCollection<PhotoUploadTask> tasks, string accommodationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var changed = 0;
        foreach (var task in tasks.Where(t => t.Status == PhotoUploadStatus.Uploading && !string.IsNullOrEmpty(t.OctorateQueueId)))
        {
            var entry = await client.GetPhotoQueueAsync(accommodationId, task.OctorateQueueId!, ct);
            if (entry is null)
            {
                task.Status = PhotoUploadStatus.Error;
                task.ErrorMessage = "The channel manager does not know the entry of the photo queue.";
                task.CompletedAt = Now();
            }
            else if (entry.State == OctoratePhotoQueueState.Waiting)
            {
                continue;
            }
            else
            {
                Apply(task, entry);
            }

            changed++;
            await db.SaveChangesAsync(ct);
        }

        changed += await RemoveAsync(tasks.Where(t => t.Status == PhotoUploadStatus.RemovePending), accommodationId, ct);
        return changed;
    }

    private async Task<int> RemoveAsync(IEnumerable<PhotoUploadTask> tasks, string accommodationId, CancellationToken ct)
    {
        var removed = 0;
        foreach (var task in tasks.ToList())
        {
            var name = task.OctoratePhotoId;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(task.OctorateQueueId))
            {
                // The photo may still be on its way: when the queue says it is done, its name is the one to delete; while it waits, nothing yet.
                var entry = await client.GetPhotoQueueAsync(accommodationId, task.OctorateQueueId, ct);
                if (entry is { State: OctoratePhotoQueueState.Waiting })
                    continue;

                name = entry?.State == OctoratePhotoQueueState.Done ? entry.PhotoName : null;
            }

            if (!string.IsNullOrEmpty(name))
                await client.DeletePhotoAsync(accommodationId, name, ct);
            else if (!string.IsNullOrEmpty(task.OctorateQueueId))
                logger.LogWarning("Photo task {TaskId} of property {PropertyId}: no name to delete the photo with, it is marked removed", task.Id, task.PropertyId);

            task.Status = PhotoUploadStatus.Removed;
            task.CompletedAt = Now();
            removed++;
            await db.SaveChangesAsync(ct);
        }

        return removed;
    }

    private void Apply(PhotoUploadTask task, OctoratePhotoQueueEntry entry)
    {
        task.OctorateQueueId = entry.Id;
        switch (entry.State)
        {
            case OctoratePhotoQueueState.Done:
                task.Status = PhotoUploadStatus.Done;
                task.OctoratePhotoId = entry.PhotoName;
                task.ErrorMessage = null;
                task.CompletedAt = Now();
                break;
            case OctoratePhotoQueueState.Error:
                task.Status = PhotoUploadStatus.Error;
                task.ErrorMessage = Cut(entry.Reason ?? "The channel manager could not take the photo.", PhotoUploadTask.ErrorMessageMaxLength);
                task.CompletedAt = Now();
                break;
            default:
                task.Status = PhotoUploadStatus.Uploading;
                task.ErrorMessage = null;
                task.CompletedAt = null;
                break;
        }
    }

    private static string Cut(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
