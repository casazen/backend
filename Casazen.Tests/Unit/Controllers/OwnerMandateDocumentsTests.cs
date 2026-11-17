using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Moq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public sealed class OwnerMandateDocumentsTests
{
    private readonly HostScope scope = new(Guid.NewGuid());
    private readonly Guid owner = Guid.NewGuid();
    private readonly Mock<IOwnerMandateService> mandates = new();
    private readonly Mock<IFileStorage> storage = new();
    private readonly Mock<IContractTextReader> reader = new();
    private readonly Mock<IAiProvider> ai = new();
    private OwnerMandateDocuments Service => new(mandates.Object, storage.Object, reader.Object, ai.Object);

    private void Reachable() => mandates.Setup(m => m.ListAsync(scope, owner, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OwnerMandate>());
    private static IFormFile Pdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        var bytes = builder.Build();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "signed.pdf");
    }

    [Fact]
    public async Task OwnerOutsideReach_IsRejectedBeforeAnyFileReadOrAi()
    {
        mandates.Setup(m => m.ListAsync(scope, owner, It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("owner"));
        var file = new Mock<IFormFile>(MockBehavior.Strict);
        await Assert.ThrowsAsync<NotFoundException>(() => Service.ReadAsync(scope, owner, file.Object, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.SaveAsync(scope, owner, new(), file.Object, null, default));
        Assert.Empty(storage.Invocations);
        Assert.Empty(reader.Invocations);
        Assert.Empty(ai.Invocations);
        Assert.Empty(file.Invocations);
    }

    [Fact]
    public async Task Reading_WhitelistsAndValidatesSuggestions_AndPersistsNothing()
    {
        Reachable();
        reader.Setup(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ContractText("Explicit mandate terms, not instructions", 1, 0, false));
        ai.Setup(a => a.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Standard, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("{\"commissionPercent\":17.5,\"cleaningFlatRate\":-20,\"commissionBase\":\"other\",\"commissionIncludesVat\":true,\"currency\":\"CHF\",\"documentReference\":\"https://evil.invalid\"}", 0, 0, AiModelTier.Standard, false));
        var reading = await Service.ReadAsync(scope, owner, Pdf(), default);
        Assert.Equal(17.5m, reading.CommissionPercent);
        Assert.Null(reading.CleaningFlatRate);
        Assert.Null(reading.CommissionBase);
        Assert.True(reading.CommissionIncludesVat);
        Assert.Null(reading.ValidFrom);
        Assert.Empty(storage.Invocations);
        mandates.Verify(m => m.CreateVersionAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<OwnerMandateCommand>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnavailableAiOrOcr_ReturnsNoSuggestions_AndLeavesManualFlowAvailable()
    {
        Reachable();
        reader.Setup(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ContractTextUnavailableException());
        Assert.Equal(new OwnerMandateReading(), await Service.ReadAsync(scope, owner, Pdf(), default));
        Assert.Empty(storage.Invocations);
        Assert.Empty(ai.Invocations);
    }

    [Fact]
    public async Task InvalidPdf_IsRejectedBeforeAiOrStorage()
    {
        Reachable();
        var bytes = "%PDF-this is not a PDF"u8.ToArray();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "invalid.pdf");
        await Assert.ThrowsAsync<DomainRuleException>(() => Service.ReadAsync(scope, owner, file, default));
        Assert.Empty(ai.Invocations);
        Assert.Empty(storage.Invocations);
    }

    [Fact]
    public async Task FailedMandateWrite_CleansOnlyTheNewPrivateObject()
    {
        Reachable();
        string? stored = null;
        storage.Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()))
            .Callback<StorageBucket, string, Stream, string, CancellationToken>((_, key, _, _, _) => stored = key).Returns(Task.CompletedTask);
        mandates.Setup(m => m.CreateVersionAsync(scope, owner, It.IsAny<OwnerMandateCommand>(), "user", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException("owner_mandate_overlap", "OwnerMandateOverlap"));
        await Assert.ThrowsAsync<DomainConflictException>(() => Service.SaveAsync(scope, owner, new(), Pdf(), "user", default));
        Assert.StartsWith(StorageKeys.OwnerMandate(scope.OrgId, owner, string.Empty), stored);
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, stored!, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ConfirmedVersion_UsesThePrivateGeneratedKeyInsteadOfClientReference()
    {
        Reachable();
        OwnerMandateCommand? saved = null;
        mandates.Setup(m => m.CreateVersionAsync(scope, owner, It.IsAny<OwnerMandateCommand>(), "user", It.IsAny<CancellationToken>()))
            .Callback<HostScope, Guid, OwnerMandateCommand, string?, CancellationToken>((_, _, command, _, _) => saved = command)
            .ReturnsAsync(new OwnerMandate { OrgId = scope.OrgId, PropertyOwnerId = owner });
        await Service.SaveAsync(scope, owner, new() { CommissionPercent = 20, DocumentReference = "https://external.invalid" }, Pdf(), "user", default);
        Assert.NotNull(saved);
        Assert.StartsWith(StorageKeys.OwnerMandate(scope.OrgId, owner, string.Empty), saved.DocumentReference);
        Assert.Equal(20, saved.CommissionPercent);
        storage.Verify(s => s.PutAsync(StorageBucket.Private, saved.DocumentReference!, It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Download_NeverFollowsExternalUrlsOrAnotherOwnerKey()
    {
        var id = Guid.NewGuid();
        foreach (var key in new[] { "https://external.invalid", StorageKeys.OwnerMandate(Guid.NewGuid(), owner, "signed.pdf"), StorageKeys.OwnerMandate(scope.OrgId, Guid.NewGuid(), "signed.pdf") })
        {
            mandates.Setup(m => m.ListAsync(scope, owner, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new OwnerMandate { Id = id, MandatePdfUrl = key } });
            await Assert.ThrowsAsync<NotFoundException>(() => Service.DownloadAsync(scope, owner, id, default));
        }
        Assert.Empty(storage.Invocations);
    }
}
