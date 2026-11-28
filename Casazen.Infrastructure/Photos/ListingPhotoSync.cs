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
/// org) is not caught here: the caller records it. A photo left unsent by a run that stopped (the rate limit of the org stops the sending and leaves the
/// rest <see cref="PhotoUploadStatus.Pending"/>) is taken up by the follow-up job once it has waited <see cref="PendingGrace"/>
/// (<see cref="ResumeAsync"/>).</para>
/// <para><b>Deleting in CasaZen removes the photo on the channels.</b> A task whose address is no longer in the gallery becomes
/// <see cref="PhotoUploadStatus.RemovePending"/> (the deletion of a photo marks it at once; this is the net for any other way a photo leaves) and
/// the next run deletes the photo on the channel manager. The decision is taken on the whole gallery as stored, not on the photos that pass the test of
/// <see cref="PublicPhotoUrlService"/>, so a storage setting that stops an address from passing can never remove photos.</para>
/// <para><b>Never «removed» without the DELETE.</b> A photo is deleted on the channel manager by the name the channel manager gave it. When the queue
/// says <c>DONE</c> and gives no name, the task stays <see cref="PhotoUploadStatus.Done"/> (the photo is there, and is never sent again) and one «To do»
/// tells the host that a photo removed here will have to be removed there by hand; and a photo that has to be removed and has no name to be deleted with
/// is not marked removed: its task becomes <see cref="PhotoUploadStatus.Error"/> with <see cref="NotRemovedMessage"/>, is not tried again, and the host
/// gets one «To do» for it. Only a photo the channel manager never took (never sent, or its queue entry ended in error) is removed with no call.</para>
/// </remarks>
public sealed class ListingPhotoSync(
    AppDbContext db,
    IOctorateApiClient client,
    PublicPhotoUrlService publicUrls,
    ListingTodos todos,
    TimeProvider timeProvider,
    ILogger<ListingPhotoSync> logger)
{
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(10);

    internal const string NotRemovedMessage = "The photo could not be removed on the channel manager: remove it there by hand.";

    internal const string QueueEntryUnknownMessage = "The channel manager does not know the entry of the photo queue.";

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

        foreach (var task in tasks.Where(t => !stored.Contains(t.PhotoUrl) && CanLeave(t)))
            task.Status = PhotoUploadStatus.RemovePending;

        await db.SaveChangesAsync(ct);

        await UploadAsync(
            property.Id,
            tasks.Where(t => t.Status is PhotoUploadStatus.Pending or PhotoUploadStatus.Error && gallery.Contains(t.PhotoUrl, StringComparer.Ordinal)).ToList(),
            accommodationId,
            ct);

        await RemoveAsync(tasks.Where(t => t.Status == PhotoUploadStatus.RemovePending), accommodationId, ct);
    }

    public async Task<int> ResumeAsync(Property property, IReadOnlyCollection<PhotoUploadTask> tasks, string accommodationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(tasks);

        var gallery = publicUrls.ListingPhotoUrls(property);
        var pending = tasks.Where(t => t.Status == PhotoUploadStatus.Pending && gallery.Contains(t.PhotoUrl, StringComparer.Ordinal)).ToList();
        await UploadAsync(property.Id, pending, accommodationId, ct);
        return pending.Count(t => t.Status != PhotoUploadStatus.Pending);
    }

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
                task.ErrorMessage = QueueEntryUnknownMessage;
                task.CompletedAt = Now();
            }
            else if (entry.State == OctoratePhotoQueueState.Waiting)
            {
                continue;
            }
            else
            {
                await ApplyAsync(task, entry, ct);
            }

            changed++;
            await db.SaveChangesAsync(ct);
        }

        changed += await RemoveAsync(tasks.Where(t => t.Status == PhotoUploadStatus.RemovePending), accommodationId, ct);
        return changed;
    }

    private async Task UploadAsync(Guid propertyId, IReadOnlyCollection<PhotoUploadTask> tasks, string accommodationId, CancellationToken ct)
    {
        foreach (var task in tasks)
        {
            try
            {
                await ApplyAsync(task, await client.UploadPhotoAsync(accommodationId, task.PhotoUrl, ct), ct);
            }
            catch (OctorateRateLimitException)
            {
                logger.LogWarning("Photos of property {PropertyId}: the rate limit of the org stopped the sending, the rest stays pending", propertyId);
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
    }

    private async Task<int> RemoveAsync(IEnumerable<PhotoUploadTask> tasks, string accommodationId, CancellationToken ct)
    {
        var changed = 0;
        foreach (var task in tasks.ToList())
        {
            var name = task.OctoratePhotoId;
            var neverTaken = false;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(task.OctorateQueueId))
            {
                var entry = await client.GetPhotoQueueAsync(accommodationId, task.OctorateQueueId, ct);
                if (entry is { State: OctoratePhotoQueueState.Waiting })
                    continue;

                name = entry?.State == OctoratePhotoQueueState.Done ? entry.PhotoName : null;
                neverTaken = entry is { State: OctoratePhotoQueueState.Error };
            }

            if (!string.IsNullOrEmpty(name))
            {
                await client.DeletePhotoAsync(accommodationId, name, ct);
            }
            else if (!neverTaken && !string.IsNullOrEmpty(task.OctorateQueueId))
            {
                logger.LogWarning("Photo task {TaskId} of property {PropertyId}: no name to delete the photo with, it stays on the channel manager", task.Id, task.PropertyId);
                task.Status = PhotoUploadStatus.Error;
                task.ErrorMessage = NotRemovedMessage;
                task.CompletedAt = Now();
                changed++;
                await db.SaveChangesAsync(ct);
                await todos.RaisePhotoNotRemovedAsync(task, ct);
                continue;
            }

            task.Status = PhotoUploadStatus.Removed;
            task.CompletedAt = Now();
            changed++;
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static bool CanLeave(PhotoUploadTask task) =>
        task.Status is PhotoUploadStatus.Pending or PhotoUploadStatus.Uploading or PhotoUploadStatus.Done
        || (task.Status == PhotoUploadStatus.Error && task.ErrorMessage != NotRemovedMessage);

    private async Task ApplyAsync(PhotoUploadTask task, OctoratePhotoQueueEntry entry, CancellationToken ct)
    {
        task.OctorateQueueId = entry.Id;
        switch (entry.State)
        {
            case OctoratePhotoQueueState.Done:
                task.Status = PhotoUploadStatus.Done;
                task.OctoratePhotoId = string.IsNullOrWhiteSpace(entry.PhotoName) ? null : entry.PhotoName;
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

        if (task is { Status: PhotoUploadStatus.Done, OctoratePhotoId: null })
        {
            logger.LogWarning("Photo task {TaskId} of property {PropertyId}: the channel manager took the photo without naming it", task.Id, task.PropertyId);
            await todos.RaisePhotosNotNamedAsync(task.PropertyId, ct);
        }
    }

    private static string Cut(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
