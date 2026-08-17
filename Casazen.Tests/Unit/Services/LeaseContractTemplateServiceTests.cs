using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LeaseContractTemplateServiceTests
{
    [Fact]
    public async Task GeneratePdfAsync_UnapprovedRegime_Throws()
    {
        var sut = new LeaseContractTemplateService(
            Options.Create(new LeaseTemplateOptions
            {
                Variants = new Dictionary<string, LeaseTemplateVariantOptions>
                {
                    ["CedolareSecca"] = new() { VersionId = "v1", Approved = false },
                },
            }),
            Mock.Of<ILogger<LeaseContractTemplateService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GeneratePdfAsync(new LeaseContract { FiscalRegime = FiscalRegime.CedolareSecca }));
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedStub_ReturnsBytesWithVersion()
    {
        var sut = new LeaseContractTemplateService(
            Options.Create(new LeaseTemplateOptions
            {
                Variants = new Dictionary<string, LeaseTemplateVariantOptions>
                {
                    ["CedolareSecca"] = new() { VersionId = "dev-stub", Approved = true },
                },
            }),
            Mock.Of<ILogger<LeaseContractTemplateService>>());

        var bytes = await sut.GeneratePdfAsync(new LeaseContract
        {
            Id = Guid.NewGuid(),
            FiscalRegime = FiscalRegime.CedolareSecca,
        });

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("dev-stub", text, StringComparison.Ordinal);
        Assert.Contains("CedolareSecca", text, StringComparison.Ordinal);
    }
}
