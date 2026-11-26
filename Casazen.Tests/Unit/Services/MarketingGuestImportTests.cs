using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class MarketingGuestImportTests
{
    private static readonly string[] Headers = ["Name", "Email", "Last stay"];
    private static readonly Dictionary<string, int> Mapping = new() { ["name"] = 0, ["email"] = 1, ["lastStay"] = 2 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_matches_preview_and_keeps_imported_addresses_unconfirmed(bool excel)
    {
        using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var property = new Property { OrgId = Guid.NewGuid() };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var importer = new MarketingGuestImport(db, CrmTestIndex.Create(), Options.Create(new CrmOptions()), CrmTestWorld.Clock());
        var scope = new HostScope(property.OrgId);
        var name = excel ? "file.xlsx" : "file.csv";
        var content = excel
            ? XlsxWriter.Write("Guests", Headers, [new object?[] { "Ada", "ada@example.com", "2026-01-01" }])
            : Encoding.UTF8.GetBytes("Name,Email,Last stay\nAda,ada@example.com,2026-01-01");
        var preview = MarketingGuestImport.Preview(name, content);
        Assert.Equal(Headers, preview.Headers);
        Assert.Single(preview.Sample);
        var result = await importer.ImportAsync(scope, property.Id, name, content, Mapping, "softOptIn", "archivio 2025", default);
        Assert.Equal(1, result.Imported);
        var contact = await db.GuestContacts.SingleAsync();
        Assert.Equal(GuestConsentSource.HistoricalImport, contact.ConsentSource);
        Assert.Equal("archivio 2025", contact.BasisVersion);
        Assert.Equal(GuestContactLegalBasis.SoftOptIn, contact.LegalBasis);
        Assert.False(CrmContactRules.MarketableEmail.Compile()(contact));
        Assert.Equal(CrmContactRules.ReasonNotConfirmed, CrmContactRules.ReachabilityReason(contact));
        var retry = await importer.ImportAsync(scope, property.Id, name, content, Mapping, "consent", "new source", default);
        Assert.Equal(1, retry.Skipped);
        Assert.Equal(1, await db.GuestCrmProfiles.CountAsync());
        Assert.Equal(new[] { property.Id }, JsonSerializer.Deserialize<Guid[]>((await db.GuestCrmProfiles.SingleAsync()).PropertyIdsJson));
    }

    [Fact]
    public async Task Import_checks_scope_and_preserves_suppression_and_ota_forward()
    {
        using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var property = new Property { OrgId = Guid.NewGuid() };
        db.Properties.Add(property);
        var index = CrmTestIndex.Create();
        db.SuppressionListEntries.Add(new SuppressionListEntry { OrgId = property.OrgId, EmailHash = index.Hash("guest@example.com") });
        await db.SaveChangesAsync();
        var importer = new MarketingGuestImport(db, index, Options.Create(new CrmOptions { OtaForwardDomains = ["relay.example.com"] }), CrmTestWorld.Clock());
        var content = Encoding.UTF8.GetBytes("Name,Email,Last stay\nA,guest@example.com,2026-01-01\nB,test@relay.example.com,2026-01-02\nC,,2026-01-03");
        await Assert.ThrowsAsync<DomainForbiddenException>(() => importer.ImportAsync(new(Guid.NewGuid()), property.Id, "a.csv", content, Mapping, "consent", "archive", default));
        var result = await importer.ImportAsync(new(property.OrgId), property.Id, "a.csv", content, Mapping, "consent", "archive", default);
        Assert.Equal(2, result.Imported);
        Assert.Equal(4, Assert.Single(result.Errors).Row);
        var contacts = await db.GuestContacts.ToListAsync();
        Assert.True(contacts.Single(c => c.Value == "guest@example.com").IsSuppressed);
        Assert.Equal(GuestContactOrigin.OtaForward, contacts.Single(c => c.Value == "test@relay.example.com").Origin);
    }

    [Fact]
    public void Interest_evidence_exposes_only_topic_counts_and_accepted_tags_reach_segments()
    {
        var evidence = MarketingGuestCategoryService.Evidence(["Ada, ada@example.com: concerto jazz. Cinema?", "Vino e degustazione"]);
        Assert.Equal(3, evidence.Count);
        Assert.Equal(1, evidence["music"]);
        Assert.Equal(1, evidence["cinema"]);
        Assert.Equal(1, evidence["food-and-wine"]);
        var profile = new GuestCrmProfile
        {
            TagsJson = "[\"returning\"]",
            MarketingCategoryOverridesJson = JsonSerializer.Serialize(new MarketingCategoryChoices { Interests = ["music"], SuggestedInterests = ["cinema"] }),
        };
        Assert.Equal(["returning", "music"], MarketingGuestCategories.EffectiveTags(profile));
        Assert.Equal("loyal", MarketingGuestCategories.Loyalty(3, CrmTestWorld.Day(-7), CrmTestWorld.Day(0), 3, 24));
        Assert.Equal("lost", MarketingGuestCategories.Loyalty(3, CrmTestWorld.Day(-900), CrmTestWorld.Day(0), 3, 24));
    }
}
