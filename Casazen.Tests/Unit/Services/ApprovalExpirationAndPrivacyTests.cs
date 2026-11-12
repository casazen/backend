using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F04/US01 (NF-68, NF-06, ADR-032-R3): what happens to the "To do" of an item that expires, and the privacy of the text of an item at rest (encrypted with the purpose of the
/// words of the conversations, never in clear in the database).
/// </summary>
public class ApprovalExpirationAndPrivacyTests : IDisposable
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private readonly ApprovalQueueRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ─── Expiry ───

    [Fact]
    public async Task Expiry_ExpiresTheItemsPastTheirTime_AndClosesTheirTodo_OthersStayAsTheyAre()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var old = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Clock.Advance(TimeSpan.FromDays(5));
        var recent = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Clock.Advance(TimeSpan.FromDays(3));

        await new ApprovalExpirationJob(_rig.Db, _rig.Clock, NullLogger<ApprovalExpirationJob>.Instance, _rig.Activities()).ProcessExpirationsAsync();

        var items = await _rig.Db.ApprovalQueueItems.AsNoTracking().ToDictionaryAsync(i => i.Id);
        Assert.Equal(ApprovalStatus.Expired, items[old.Id].Status);
        Assert.Equal(ApprovalStatus.Pending, items[recent.Id].Status);
        var todos = await _rig.Db.InternalActivities.AsNoTracking().ToDictionaryAsync(a => a.EntityId!.Value);
        Assert.NotNull(todos[old.Id].CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, todos[old.Id].ClosedBy);
        Assert.Null(todos[recent.Id].CompletedAt);
        Assert.Equal([recent.Id], (await service.GetPendingAsync(_rig.OrgWide)).Select(i => i.Id));
    }

    [Fact]
    public async Task Expiry_WhenTheTodoIsGoneOrTheItemHasNone_StillExpiresTheItem()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var withoutTodo = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo", isAiGenerated: true);
        var closedByHand = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await _rig.Activities().CloseAsync(closedByHand.ActivityId!.Value, ApprovalQueueRig.Owner, CancellationToken.None);
        (await _rig.Db.ApprovalQueueItems.SingleAsync(i => i.Id == withoutTodo.Id)).ActivityId = null;
        (await _rig.Db.ApprovalQueueItems.SingleAsync(i => i.Id == closedByHand.Id)).ActivityId = Guid.NewGuid();
        await _rig.Db.SaveChangesAsync();
        _rig.Clock.Advance(TimeSpan.FromDays(8));

        await new ApprovalExpirationJob(_rig.Db, _rig.Clock, NullLogger<ApprovalExpirationJob>.Instance, _rig.Activities()).ProcessExpirationsAsync();

        Assert.All(await _rig.Db.ApprovalQueueItems.AsNoTracking().ToListAsync(), i => Assert.Equal(ApprovalStatus.Expired, i.Status));
    }

    [Fact]
    public async Task Expiry_ACloseThatFails_StopsTheJobWithTheItemsStillPending_SoTheRetryFindsThem()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Clock.Advance(TimeSpan.FromDays(8));
        var broken = new Mock<IInternalActivityService>();
        broken
            .Setup(a => a.CloseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database went away"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalExpirationJob(_rig.Db, _rig.Clock, NullLogger<ApprovalExpirationJob>.Instance, broken.Object).ProcessExpirationsAsync());

        Assert.Equal(ApprovalStatus.Pending, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Status);
        await new ApprovalExpirationJob(_rig.Db, _rig.Clock, NullLogger<ApprovalExpirationJob>.Instance, _rig.Activities()).ProcessExpirationsAsync();
        Assert.Equal(ApprovalStatus.Expired, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Status);
        Assert.NotNull((await _rig.Db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == item.Id)).CompletedAt);
    }

    // ─── Privacy at rest (ADR-032-R3) ───

    /// <summary>
    /// Privacy rule: the text of an item (a message to a guest, which may name the guest or hold an IBAN) and the host's edit of it are stored encrypted, with the purpose of the words of
    /// the conversations; what the column holds is a Data Protection payload with none of the words, and only the converter of that purpose reads it back. The reason of a rejection is
    /// the host's own note, a column with a length, and stays in clear.
    /// </summary>
    [Fact]
    public void ApprovalQueueItem_TextEncryptedAtRest_NotPlaintext_Test()
    {
        const string text = "Gentile Zefirino Ospite, abbiamo ricevuto il suo IBAN IT60X0542811101000000123456";
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var entity = db.Model.FindEntityType(typeof(ApprovalQueueItem))!;
        var otherPurpose = Provider.CreateProtector(EncryptedColumns.OtaSecretsPurpose);

        foreach (var property in new[] { nameof(ApprovalQueueItem.ContentText), nameof(ApprovalQueueItem.EditedContentText) })
        {
            Assert.Contains(
                EncryptedColumns.All,
                c => c.EntityType == typeof(ApprovalQueueItem) && c.Property == property && c.Purpose == EncryptedColumns.OtaMessageTextPurpose);

            var column = entity.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
            Assert.Null(column.GetMaxLength());

            var stored = (string)converter.ConvertToProvider(text)!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("Zefirino", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("IT60X0542811101000000123456", stored, StringComparison.Ordinal);
            Assert.Equal(text, converter.ConvertFromProvider(stored));
            Assert.ThrowsAny<CryptographicException>(() => otherPurpose.Unprotect(stored));
        }

        Assert.Equal("Casazen.Message.Content", EncryptedColumns.OtaMessageTextPurpose);
        Assert.Null(entity.FindProperty(nameof(ApprovalQueueItem.RejectionReason))!.GetValueConverter());
        Assert.Equal(ApprovalQueueItem.RejectionReasonMaxLength, entity.FindProperty(nameof(ApprovalQueueItem.RejectionReason))!.GetMaxLength());
        Assert.True(EncryptedColumns.IsConfigured(db));
    }

    /// <summary>The rows of before the text was encrypted are read as they are (and rewritten at startup): a clear value is not a payload.</summary>
    [Fact]
    public void ApprovalQueueItem_ATextWrittenInClearBeforeTheEncryption_IsReadAsItIs()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var converter = Assert.IsType<EncryptedStringConverter>(
            db.Model.FindEntityType(typeof(ApprovalQueueItem))!.FindProperty(nameof(ApprovalQueueItem.ContentText))!.GetValueConverter());

        Assert.True(EncryptedColumns.IsLegacyPlaintext("Oggetto\n\n<p>Testo</p>"));
        Assert.Equal("Oggetto\n\n<p>Testo</p>", converter.ConvertFromProvider("Oggetto\n\n<p>Testo</p>"));
    }
}
