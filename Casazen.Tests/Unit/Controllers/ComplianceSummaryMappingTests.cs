using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>GET api/compliance/summary</c> with the traffic light (E03/F02/US01/T03, NF-52): the light, the reason, the period, the evidence and the
/// handoff topic reach the client, the new sections are named, and everything an older client reads is still there under the same names
/// (the contract is add-only).
/// </summary>
public class ComplianceSummaryMappingTests
{
    private static readonly Guid OrgId = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281702");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task GetSummary_MapsTheLightAndTheNewSections_AndKeepsEverythingAnOlderClientReads()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var verifiedAt = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
        var dueAt = new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

        var old = ComplianceSummaryItem.ForBooking(ComplianceCockpitAction.SendAlloggiati, bookingId, "Anna Verdi") with
        {
            DueAt = dueAt,
            Light = ComplianceLight.Red,
            Reason = ComplianceReasonCodes.DeadlineOverdue,
            EvidenceClass = ComplianceEvidenceClass.Missing,
        };
        var entrusted = ComplianceSummaryItem.ForBooking(ComplianceCockpitAction.UploadAlloggiatiReceipt, bookingId, "Anna Verdi") with
        {
            Light = ComplianceLight.Yellow,
            Reason = ComplianceReasonCodes.EntrustedToChannelManager,
            EvidenceClass = ComplianceEvidenceClass.ExternalVerificationRequired,
            EvidenceSource = ComplianceEvidenceSources.Octorate,
            HandoffTopic = OctorateHandoffTopic.Alloggiati,
        };
        var period = ComplianceSummaryItem.ForProperty(ComplianceCockpitAction.DeclareTouristTax, propertyId, "Trullo") with
        {
            Light = ComplianceLight.Green,
            Reason = ComplianceReasonCodes.ReceiptArchived,
            Period = "2026-10",
            EvidenceClass = ComplianceEvidenceClass.OfficialProof,
            EvidenceSource = ComplianceEvidenceSources.ManualUpload,
            LastVerifiedAt = verifiedAt,
        };
        var empty = new ComplianceSummarySection(0, []);
        var summary = new ComplianceSummaryResult(empty, empty, empty, empty, new ComplianceSummarySection(1, [old]), empty)
        {
            ReceiptsToUpload = new ComplianceSummarySection(1, [entrusted]),
            PeriodItems = new ComplianceSummarySection(0, [period]),
        };

        var wizard = new Mock<IComplianceWizardService>();
        wizard.Setup(w => w.GetSummaryAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>())).ReturnsAsync(summary);
        var missing = new Mock<IComplianceMissingService>();
        missing
            .Setup(m => m.DescribeAsync(It.IsAny<ComplianceSummaryResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComplianceSummaryResult s, CancellationToken _) => s);
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(OrgId));
        var controller = new ComplianceController(wizard.Object, missing.Object, orgs.Object, scopes.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = HostAuthorizationTestHarness.User("auth0|owner-a", "PropertyOwner") },
            },
        };

        var ok = Assert.IsType<OkObjectResult>((await controller.GetSummary(CancellationToken.None)).Result);
        var dto = Assert.IsType<ComplianceSummaryDto>(ok.Value);

        // The fields of the item that an older client reads are the same, under the same names.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(dto, Json));
        var root = document.RootElement;
        string[] older = ["propertiesPending", "guestCheckInsIncomplete", "checkoutsDue", "alloggiatiFailures", "alloggiatiManualRequired", "turnoversPending"];
        string[] added = ["identityPending", "receiptsToUpload", "manualSendsRequired", "connectionsToFix", "periodItems"];
        foreach (var name in older.Concat(added))
            Assert.True(root.TryGetProperty(name, out var section) && section.TryGetProperty("count", out _) && section.TryGetProperty("items", out _), name);

        var first = root.GetProperty("alloggiatiManualRequired").GetProperty("items")[0];
        Assert.Equal("SendAlloggiati", first.GetProperty("action").GetString());
        Assert.Equal(bookingId, first.GetProperty("bookingId").GetGuid());
        Assert.Equal(bookingId, first.GetProperty("id").GetGuid());
        Assert.Equal("Anna Verdi", first.GetProperty("label").GetString());
        Assert.Equal(dueAt, first.GetProperty("dueAt").GetDateTime());
        Assert.True(first.TryGetProperty("propertyId", out _) && first.TryGetProperty("missing", out _));

        // The new members: the light and the reason on an item of the older sections too.
        Assert.Equal(("Red", ComplianceReasonCodes.DeadlineOverdue, "Missing"),
            (first.GetProperty("light").GetString(), first.GetProperty("reason").GetString(), first.GetProperty("evidenceClass").GetString()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("period").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("handoffTopic").ValueKind);

        var receipt = root.GetProperty("receiptsToUpload").GetProperty("items")[0];
        Assert.Equal(("UploadAlloggiatiReceipt", "Yellow", "entrusted_to_channel_manager", "ExternalVerificationRequired", "Alloggiati"),
            (receipt.GetProperty("action").GetString(), receipt.GetProperty("light").GetString(), receipt.GetProperty("reason").GetString(),
                receipt.GetProperty("evidenceClass").GetString(), receipt.GetProperty("handoffTopic").GetString()));

        var tax = root.GetProperty("periodItems").GetProperty("items")[0];
        Assert.Equal(("DeclareTouristTax", "Green", "2026-10", "OfficialProof", "manual_upload"),
            (tax.GetProperty("action").GetString(), tax.GetProperty("light").GetString(), tax.GetProperty("period").GetString(),
                tax.GetProperty("evidenceClass").GetString(), tax.GetProperty("evidenceSource").GetString()));
        Assert.Equal(propertyId, tax.GetProperty("propertyId").GetGuid());
        Assert.Equal(JsonValueKind.Null, tax.GetProperty("bookingId").ValueKind);
        Assert.Equal(verifiedAt, tax.GetProperty("lastVerifiedAt").GetDateTime());
        Assert.Equal(0, root.GetProperty("periodItems").GetProperty("count").GetInt32());
    }
}
