using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F02/US03/T01, NF-56, MKT-03, S18, Q-006: the hand-off of a Partner Hub partner. Proofs: the brief carries the whitelisted fields of the property and
/// never a guest, a booking, a price or a document, and the CIN only when it is valid (privacy); a partner the org may not see, a property outside the
/// caller's scope and a declaration for the whole org by a caller limited to some properties are refused, the first two as the 404 of something that does not
/// exist (permission); an org never reads or changes another org's activation (data); a declaration is the host's word, Active and declared with the day it
/// gave, never in the future in Rome, idempotent, and verified only by a proven Active connection of the same property while the connector is on (data, Q-006).
/// The database is in memory: what decides is the service.
/// </summary>
public class MarketplacePartnerActivationTests
{
    private const string Collaborator = "auth0|collaborator";
    private const string Host = "auth0|host";
    private const string ValidCin = "IT058091C27G5FFZDZ";
    private static readonly Guid OrgA = Guid.Parse("c3a1d7e0-5b24-4f6a-9d1e-7a8b9c0d1e21");
    private static readonly Guid OrgB = Guid.Parse("5e9f0a1b-2c3d-4e5f-8a6b-7c8d9e0f1a32");

    [Fact]
    public async Task MarketplacePartnerBrief_NeverIncludesGuestOrBookingData_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno", guideIt: "Scegli il piano base\n  Autorizza la lettura delle prenotazioni  \r\n\r\nConferma l'accesso");
        await w.AddStayAsync();

        await using var db = w.NewDb();
        var brief = await w.Service(db).GetBriefAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, "it");

        Assert.Equal("partner-uno", brief.Code);
        Assert.Equal("Partner partner-uno", brief.Name);
        Assert.Equal("https://example.com/partner-uno", brief.WebsiteUrl);
        Assert.Equal(new[] { "Scegli il piano base", "Autorizza la lettura delle prenotazioni", "Conferma l'accesso" }, brief.Checklist);
        var data = Assert.IsType<MarketplacePartnerPropertyData>(brief.PropertyData);
        Assert.Equal(
            ("Casa Lago", "Via Prova 1", "Como", "22100", 2, 1, 4, ValidCin),
            (data.Name, data.Address, data.City, data.PostalCode, data.Bedrooms, data.Bathrooms, data.MaxGuests, data.CinCode));

        var json = JsonSerializer.Serialize(MarketplacePartnerBriefDto.From(brief), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "checklist", "code", "name", "propertyData", "websiteUrl" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            new[] { "address", "bathrooms", "bedrooms", "cinCode", "city", "maxGuests", "name", "postalCode" },
            document.RootElement.GetProperty("propertyData").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            new[] { "Address", "Bathrooms", "Bedrooms", "CinCode", "City", "MaxGuests", "Name", "PostalCode" },
            typeof(MarketplacePartnerPropertyData).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());

        foreach (var secret in new[]
                 {
                     "Mario-TEST", "Rossi-TEST", "mario.rossi.test@example.com", "333 0000000", "DOCUMENTO-DI-TEST-123", "BK-TEST-0001", "EXT-TEST-0001",
                     "RICHIESTA-RISERVATA", "1234.56", "1111.11", "199.99", "77.77", "DESCRIZIONE-RISERVATA", "REGOLE-RISERVATE", "FISCALE-RISERVATO", "int. 9",
                 })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MarketplacePartnerBrief_TheCin_IsThereOnlyWhenItIsValid_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");

        // A valid CIN as it was stored with spaces and in lower case comes out normalized; an invalid one, the invented old format and none come out as nothing.
        var cases = new (string? Stored, string? Expected)[]
        {
            ("it 058091 c27 g5ffzdz", ValidCin),
            (ValidCin, ValidCin),
            ("IT-12345-0123456789", null),
            ("NOT-A-CIN", null),
            ("   ", null),
            (null, null),
        };
        foreach (var (stored, expected) in cases)
        {
            await w.SetCinAsync(stored);
            await using var db = w.NewDb();
            var brief = await w.Service(db).GetBriefAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId);
            Assert.Equal(expected, brief.PropertyData!.CinCode);

            var json = JsonSerializer.Serialize(MarketplacePartnerBriefDto.From(brief), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(expected is not null, json.Contains(ValidCin, StringComparison.Ordinal));
            Assert.DoesNotContain("NOT-A-CIN", json, StringComparison.Ordinal);
            Assert.DoesNotContain("12345-0123456789", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MarketplacePartner_TheOrgMayNotSee_IsA404_OnTheBriefAndOnTheDeclaration_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("open");
        await w.AddPartnerAsync("hidden", visible: false);
        await w.AddPartnerAsync("starter-only", plans: [PlanTier.Starter]);
        await w.AddPartnerAsync("switzerland-only", countries: ["CH"]);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            foreach (var code in new[] { "hidden", "starter-only", "switzerland-only", "nobody-has-this-code", " ", string.Empty })
            {
                var brief = await Assert.ThrowsAsync<NotFoundException>(() => service.GetBriefAsync(OrgA, w.OrgWide, code, w.PropertyId));
                Assert.Equal("marketplace_entry_not_found", brief.Code);

                var declared = await Assert.ThrowsAsync<NotFoundException>(
                    () => service.DeclareAsync(OrgA, w.OrgWide, code, w.PropertyId, new DateOnly(2026, 11, 3), Host));
                Assert.Equal("marketplace_entry_not_found", declared.Code);
            }
        }

        Assert.Empty(await w.ActivationsAsync());
        await using (var db = w.NewDb())
        {
            Assert.Equal("open", (await w.Service(db).GetBriefAsync(OrgA, w.OrgWide, "open", null)).Code);
            Assert.Equal("open", (await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "open", null, new DateOnly(2026, 11, 3), Host)).PartnerCode);
        }
    }

    [Fact]
    public async Task MarketplacePartner_PropertyOutsideTheScope_IsA404_AndNothingIsWritten_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");
        await w.SeedOtherOrgAsync();

        await w.GrantAsync(Collaborator, w.PropertyId);
        var scope = new HostScope(OrgA, GrantedToUserId: Collaborator);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            foreach (var property in new[] { w.OtherPropertyId, w.PropertyOfB, Guid.NewGuid(), Guid.Empty })
            {
                var steps = new Func<Task>[]
                {
                    () => service.GetBriefAsync(OrgA, scope, "partner-uno", property),
                    () => service.DeclareAsync(OrgA, scope, "partner-uno", property, new DateOnly(2026, 11, 3), Collaborator),
                    () => service.UndeclareAsync(OrgA, scope, "partner-uno", property, Collaborator),
                };
                foreach (var step in steps)
                {
                    var refused = await Assert.ThrowsAsync<NotFoundException>(step);
                    Assert.Equal("marketplace_property_not_found", refused.Code);
                }
            }

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.DeclareAsync(OrgA, new HostScope(OrgB), "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Collaborator));
        }

        Assert.Empty(await w.ActivationsAsync());

        await using (var db = w.NewDb())
        {
            var declared = await w.Service(db).DeclareAsync(OrgA, scope, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Collaborator);
            Assert.Equal(w.PropertyId, declared.PropertyId);
            Assert.NotNull((await w.Service(db).GetBriefAsync(OrgA, scope, "partner-uno", w.PropertyId)).PropertyData);
        }

        Assert.Equal(w.PropertyId, Assert.Single(await w.ActivationsAsync()).PropertyId);
    }

    [Fact]
    public async Task MarketplacePartner_ForTheWholeOrg_NeedsAScopeOverTheWholeOrg_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");
        await w.GrantAsync(Collaborator, w.PropertyId);

        var limited = new[] { new HostScope(OrgA, GrantedToUserId: Collaborator), new HostScope(OrgA, OwnerId: "auth0|owner") };
        foreach (var scope in limited)
        {
            await using var db = w.NewDb();
            var service = w.Service(db);
            var declared = await Assert.ThrowsAsync<DomainForbiddenException>(
                () => service.DeclareAsync(OrgA, scope, "partner-uno", null, new DateOnly(2026, 11, 3), Collaborator));
            Assert.Equal("marketplace_partner_needs_whole_org", declared.Code);
            var undeclared = await Assert.ThrowsAsync<DomainForbiddenException>(() => service.UndeclareAsync(OrgA, scope, "partner-uno", null, Collaborator));
            Assert.Equal("marketplace_partner_needs_whole_org", undeclared.Code);

            Assert.Null((await service.GetBriefAsync(OrgA, scope, "partner-uno", null)).PropertyData);
        }

        Assert.Empty(await w.ActivationsAsync());

        await using (var db = w.NewDb())
        {
            var declared = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 3), Host);
            Assert.Null(declared.PropertyId);
        }

        Assert.Null(Assert.Single(await w.ActivationsAsync()).PropertyId);
    }

    [Fact]
    public async Task MarketplacePartner_AnotherOrg_IsNeverReadOrChanged_Test()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");
        await w.SeedOtherOrgAsync();
        await w.AddRowAsync(Row(OrgB, null, MarketplaceActivationKind.Partner, "partner-uno", MarketplaceActivationStatus.Active, declared: true));
        await w.AddRowAsync(Row(OrgB, w.PropertyOfB, MarketplaceActivationKind.Partner, "partner-uno", MarketplaceActivationStatus.ToFix, declared: true));
        var ofB = await w.RowsAsync(OrgB);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            var declared = await service.DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 3), Host);
            Assert.Equal(MarketplaceActivationStatus.Active, declared.Status);
            Assert.True(declared.IsDeclared);
            Assert.Null(declared.VerifiedAt);

            Assert.Equal(MarketplaceActivationStatus.Deactivated, (await service.UndeclareAsync(OrgA, w.OrgWide, "partner-uno", null, Host)).Status);
            var named = await Assert.ThrowsAsync<NotFoundException>(
                () => service.DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyOfB, new DateOnly(2026, 11, 3), Host));
            Assert.Equal("marketplace_property_not_found", named.Code);
            var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.UndeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, Host));
            Assert.Equal("marketplace_activation_not_found", missing.Code);
        }

        Assert.Equal(ofB, await w.RowsAsync(OrgB));
        Assert.Equal(OrgA, Assert.Single(await w.ActivationsAsync()).OrgId);
    }

    [Fact]
    public async Task TheDeclaration_RecordsActiveAndDeclared_WithTheDayGiven_AndDeclaringTwiceChangesNothing()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");

        MarketplacePartnerActivation first;
        await using (var db = w.NewDb())
            first = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 3), Host);

        Assert.Equal(MarketplaceActivationStatus.Active, first.Status);
        Assert.True(first.IsDeclared);
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), first.DeclaredAt);
        Assert.Null(first.VerifiedAt);
        Assert.Equal("partner-uno", first.PartnerCode);
        Assert.Null(first.PropertyId);

        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(first.ActivationId, row.Id);
        Assert.Equal(MarketplaceActivationKind.Partner, row.Kind);
        Assert.Equal("partner-uno", row.TargetCode);
        Assert.Equal(w.Clock.Now, row.CreatedAt);
        Assert.Null(row.RequestedAt);
        Assert.Null(row.LastSyncedAt);
        var stored = (await w.RowsAsync()).Single();

        w.Clock.Set(new DateTimeOffset(2026, 11, 9, 9, 30, 0, TimeSpan.Zero));
        foreach (var day in new[] { new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 9) })
        {
            await using var db = w.NewDb();
            var again = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, day, Collaborator);
            Assert.Equal(first, again);
        }

        Assert.Equal(stored, (await w.RowsAsync()).Single());

        await using (var db = w.NewDb())
        {
            var ofProperty = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 5), Host);
            Assert.Equal(w.PropertyId, ofProperty.PropertyId);
            Assert.NotEqual(first.ActivationId, ofProperty.ActivationId);
            Assert.Equal(ofProperty, await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 6), Host));
        }

        Assert.Equal(2, (await w.ActivationsAsync()).Count);

        var logged = w.Logs.AllOutput;
        Assert.Contains("partner-uno", logged, StringComparison.Ordinal);
        foreach (var secret in new[] { "Casa Lago", "Via Prova", "Como", ValidCin })
            Assert.DoesNotContain(secret, logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeclaration_InTheFuture_IsA422_AndTodayInRomeIsNot()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");

        w.Clock.Set(new DateTimeOffset(2026, 11, 6, 23, 30, 0, TimeSpan.Zero));
        await using (var db = w.NewDb())
        {
            var service = w.Service(db);
            foreach (var day in new[] { new DateOnly(2026, 11, 8), new DateOnly(2027, 1, 1), DateOnly.MaxValue })
            {
                var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, day, Host));
                Assert.Equal("marketplace_declared_date_invalid", refused.Code);
                Assert.Equal("MarketplaceDeclaredDateInvalid", refused.MessageKey);
            }
        }

        Assert.Empty(await w.ActivationsAsync());

        await using (var db = w.NewDb())
        {
            var today = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 7), Host);
            Assert.Equal(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), today.DeclaredAt);
        }
    }

    [Fact]
    public async Task TheDeclaration_WithAProvenActiveConnectionOfTheSameProperty_IsVerifiedNotDeclared()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno", portalCode: "portal-uno");
        await w.AddRowAsync(Row(OrgA, w.PropertyId, MarketplaceActivationKind.Tool, "portal-uno", MarketplaceActivationStatus.Active, verified: true));

        await using (var db = w.NewDb())
        {
            var declared = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Host);

            Assert.Equal(MarketplaceActivationStatus.Active, declared.Status);
            Assert.False(declared.IsDeclared);
            Assert.Equal(w.Clock.Now, declared.VerifiedAt);

            Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), declared.DeclaredAt);
        }

        var channel = await World.CreateAsync();
        await channel.AddPartnerAsync("partner-uno", portalCode: "portal-uno");
        await channel.AddRowAsync(Row(OrgA, channel.PropertyId, MarketplaceActivationKind.Channel, "portal-uno", MarketplaceActivationStatus.Active, verified: true));
        await using (var db = channel.NewDb())
            Assert.False((await channel.Service(db).DeclareAsync(OrgA, channel.OrgWide, "partner-uno", channel.PropertyId, new DateOnly(2026, 11, 3), Host)).IsDeclared);
    }

    [Theory]
    [InlineData("OtherProperty")]
    [InlineData("Pending")]
    [InlineData("ToFix")]
    [InlineData("Deactivated")]
    [InlineData("DeclaredByTheHost")]
    [InlineData("LicensedFunction")]
    [InlineData("PartnerRow")]
    [InlineData("OtherCode")]
    [InlineData("AnotherOrgSameProperty")]
    [InlineData("ConnectorOff")]
    [InlineData("OrgNotConnected")]
    [InlineData("NoPortalCode")]
    [InlineData("NoProperty")]
    public async Task TheDeclaration_WithoutAProofOfTheSameProperty_StaysDeclared(string scenario)
    {
        var w = await World.CreateAsync(connected: scenario != "OrgNotConnected");
        w.ConnectorOn = scenario != "ConnectorOff";
        await w.AddPartnerAsync("partner-uno", portalCode: scenario == "NoPortalCode" ? null : "portal-uno");

        var kind = scenario switch
        {
            "LicensedFunction" => MarketplaceActivationKind.LicensedFunction,
            "PartnerRow" => MarketplaceActivationKind.Partner,
            _ => MarketplaceActivationKind.Tool,
        };
        var status = scenario switch
        {
            "Pending" => MarketplaceActivationStatus.Pending,
            "ToFix" => MarketplaceActivationStatus.ToFix,
            "Deactivated" => MarketplaceActivationStatus.Deactivated,
            _ => MarketplaceActivationStatus.Active,
        };
        var code = scenario == "OtherCode" ? "portal-due" : "portal-uno";
        var owner = scenario == "AnotherOrgSameProperty" ? OrgB : OrgA;
        await w.AddRowAsync(Row(owner, scenario == "OtherProperty" ? w.OtherPropertyId : w.PropertyId, kind, code, status, declared: scenario == "DeclaredByTheHost"));

        Guid? declaredFor = scenario == "NoProperty" ? null : w.PropertyId;
        await using var db = w.NewDb();
        var declared = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", declaredFor, new DateOnly(2026, 11, 3), Host);

        Assert.Equal(MarketplaceActivationStatus.Active, declared.Status);
        Assert.True(declared.IsDeclared);
        Assert.Null(declared.VerifiedAt);
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), declared.DeclaredAt);
    }

    [Fact]
    public async Task TheDeclaration_OfARowThatIsOnlyDeclared_IsVerifiedWhenAProvenConnectionAppearsLater_AndNothingElseMoves()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno", portalCode: "portal-uno");

        MarketplacePartnerActivation first;
        await using (var db = w.NewDb())
            first = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Host);

        Assert.True(first.IsDeclared);
        Assert.Null(first.VerifiedAt);
        var before = Assert.Single(await w.ActivationsAsync());

        await w.AddRowAsync(Row(OrgA, w.PropertyId, MarketplaceActivationKind.Tool, "portal-uno", MarketplaceActivationStatus.Active, verified: true));
        w.Clock.Set(new DateTimeOffset(2026, 11, 9, 9, 30, 0, TimeSpan.Zero));
        w.ConnectorOn = false;
        await using (var db = w.NewDb())
            Assert.Equal(first, await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 9), Collaborator));

        Assert.Equal(Describe(before), Describe(PartnerRow(await w.ActivationsAsync())));

        w.ConnectorOn = true;
        MarketplacePartnerActivation verified;
        await using (var db = w.NewDb())
            verified = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 9), Collaborator);

        Assert.Equal(first.ActivationId, verified.ActivationId);
        Assert.Equal(MarketplaceActivationStatus.Active, verified.Status);
        Assert.False(verified.IsDeclared);
        Assert.Equal<DateTime?>(w.Clock.Now, verified.VerifiedAt);
        Assert.Equal(first.DeclaredAt, verified.DeclaredAt);

        var after = PartnerRow(await w.ActivationsAsync());
        Assert.Equal(
            (before.Id, before.CreatedAt, before.DeclaredAt, before.Status, before.RequestedAt, before.LastSyncedAt, before.PropertyId),
            (after.Id, after.CreatedAt, after.DeclaredAt, after.Status, after.RequestedAt, after.LastSyncedAt, after.PropertyId));
        Assert.False(after.IsDeclared);
        Assert.Equal<DateTime?>(w.Clock.Now, after.VerifiedAt);
        Assert.Equal(w.Clock.Now, after.UpdatedAt);

        w.Clock.Set(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero));
        await using (var db = w.NewDb())
            Assert.Equal(verified, await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 10), Host));

        Assert.Equal(Describe(after), Describe(PartnerRow(await w.ActivationsAsync())));

        await using (var db = w.NewDb())
        {
            Assert.True((await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 10), Host)).IsDeclared);
            Assert.True((await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", null, new DateOnly(2026, 11, 10), Host)).IsDeclared);
        }
    }

    [Fact]
    public async Task TheGuidedDeactivation_TurnsTheRowOff_KeepsItAsHistory_AndTheHostCanDeclareAgain()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");

        Guid id;
        await using (var db = w.NewDb())
            id = (await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Host)).ActivationId;

        // Turning off a partner the admin hid since still works: the host can always turn off what it activated.
        await w.HidePartnerAsync("partner-uno");
        w.Clock.Set(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        MarketplacePartnerActivation off;
        await using (var db = w.NewDb())
            off = await w.Service(db).UndeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, Host);

        Assert.Equal(id, off.ActivationId);
        Assert.Equal(MarketplaceActivationStatus.Deactivated, off.Status);
        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(MarketplaceActivationStatus.Deactivated, row.Status);
        Assert.Equal(w.Clock.Now, row.UpdatedAt);
        var stored = (await w.RowsAsync()).Single();

        w.Clock.Set(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        await using (var db = w.NewDb())
            Assert.Equal(off, await w.Service(db).UndeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, Host));

        Assert.Equal(stored, (await w.RowsAsync()).Single());

        await using (var db = w.NewDb())
        {
            var never = await Assert.ThrowsAsync<NotFoundException>(() => w.Service(db).UndeclareAsync(OrgA, w.OrgWide, "partner-due", w.PropertyId, Host));
            Assert.Equal("marketplace_activation_not_found", never.Code);
            await Assert.ThrowsAsync<NotFoundException>(
                () => w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 9), Host));
        }

        await w.HidePartnerAsync("partner-uno", visible: true);
        await using (var db = w.NewDb())
        {
            var again = await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 9), Host);
            Assert.Equal(id, again.ActivationId);
            Assert.Equal(MarketplaceActivationStatus.Active, again.Status);
            Assert.True(again.IsDeclared);
            Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), again.DeclaredAt);
        }

        Assert.Single(await w.ActivationsAsync());
    }

    [Fact]
    public async Task TheBrief_IsInTheLanguageAsked_ItalianWhenThePartnerHasNoTextInIt_AndEmptyWithNoText()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("both", guideIt: "Passo uno\nPasso due", guideEn: "Step one\nStep two");
        await w.AddPartnerAsync("italian-only", guideIt: "Solo italiano");
        await w.AddPartnerAsync("no-text", guideIt: null);

        await using var db = w.NewDb();
        var service = w.Service(db);
        Assert.Equal(new[] { "Step one", "Step two" }, (await service.GetBriefAsync(OrgA, w.OrgWide, "both", null, "en")).Checklist);
        Assert.Equal(new[] { "Step one", "Step two" }, (await service.GetBriefAsync(OrgA, w.OrgWide, "both", null, "EN-gb")).Checklist);
        Assert.Equal(new[] { "Passo uno", "Passo due" }, (await service.GetBriefAsync(OrgA, w.OrgWide, "both", null, "it")).Checklist);
        Assert.Equal(new[] { "Passo uno", "Passo due" }, (await service.GetBriefAsync(OrgA, w.OrgWide, "both", null, "de")).Checklist);
        Assert.Equal(new[] { "Solo italiano" }, (await service.GetBriefAsync(OrgA, w.OrgWide, "italian-only", null, "en")).Checklist);
        Assert.Empty((await service.GetBriefAsync(OrgA, w.OrgWide, "no-text", null, "en")).Checklist);
        Assert.Null((await service.GetBriefAsync(OrgA, w.OrgWide, "both", null, "en")).PropertyData);
    }

    [Fact]
    public async Task ThePartnerCard_FollowsADeclarationMadeForAProperty_AndTheActiveTabListsItWithItsProperty()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno");
        await w.AddPartnerAsync("partner-due");

        await using (var db = w.NewDb())
            await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Host);

        await using (var db = w.NewDb())
        {
            var catalog = w.Catalog(db);

            Assert.Equal(MarketplaceCardStatus.Active, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, null, "it"), "partner-uno").Status);
            Assert.Equal(MarketplaceCardStatus.Active, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, w.PropertyId, "it"), "partner-uno").Status);
            var other = CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, w.OtherPropertyId, "it"), "partner-uno");
            Assert.Equal(MarketplaceCardStatus.NotActive, other.Status);
            Assert.True(other.CanActivate);
            Assert.Equal(MarketplaceStatusScope.Org, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, null, "it"), "partner-uno").StatusScope);
            Assert.Equal(MarketplaceCardStatus.NotActive, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, w.PropertyId, "it"), "partner-due").Status);

            var integration = Assert.Single((await catalog.GetActiveAsync(OrgA, w.OrgWide, null)).Integrations);
            Assert.Equal(MarketplaceActivationKind.Partner, integration.Kind);
            Assert.Equal("partner-uno", integration.Code);
            Assert.Equal(w.PropertyId, integration.Property?.Id);
            Assert.True(integration.IsDeclared);
        }

        await using (var db = w.NewDb())
            await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-due", null, new DateOnly(2026, 11, 3), Host);

        await using (var db = w.NewDb())
        {
            var catalog = w.Catalog(db);
            Assert.Equal(MarketplaceCardStatus.Active, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, w.OtherPropertyId, "it"), "partner-due").Status);
            Assert.Equal(MarketplaceCardStatus.Active, CardOf(await catalog.GetCatalogAsync(OrgA, w.OrgWide, null, "it"), "partner-due").Status);
        }

        await using (var db = w.NewDb())
            await w.Service(db).UndeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, Host);

        await using (var db = w.NewDb())
            Assert.Equal(MarketplaceCardStatus.NotActive, CardOf(await w.Catalog(db).GetCatalogAsync(OrgA, w.OrgWide, null, "it"), "partner-uno").Status);
    }

    [Fact]
    public async Task TheActiveTab_ExposesTheDayDeclaredAndTheProof_AfterAReload()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno", portalCode: "portal-uno");
        await w.AddPartnerAsync("partner-due");
        await w.AddRowAsync(Row(OrgA, w.PropertyId, MarketplaceActivationKind.Tool, "portal-uno", MarketplaceActivationStatus.Active, verified: true));

        await using (var db = w.NewDb())
        {
            await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-uno", w.PropertyId, new DateOnly(2026, 11, 3), Host);
            await w.Service(db).DeclareAsync(OrgA, w.OrgWide, "partner-due", w.PropertyId, new DateOnly(2026, 11, 4), Host);
        }

        await using var reload = w.NewDb();
        var active = await w.Catalog(reload).GetActiveAsync(OrgA, w.OrgWide, null);
        var json = JsonSerializer.Serialize(
            MarketplaceActiveResponse.From(active, new ResxMarketplaceTexts(), "it"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var byCode = document.RootElement.GetProperty("integrations").EnumerateArray().ToDictionary(item => item.GetProperty("code").GetString()!);

        string? Text(string code, string field) =>
            byCode[code].GetProperty(field) is { ValueKind: not JsonValueKind.Null } value ? value.GetString() : null;

        Assert.Equal(3, byCode.Count);
        Assert.False(byCode["partner-uno"].GetProperty("isDeclared").GetBoolean());
        Assert.Equal("2026-11-03T00:00:00Z", Text("partner-uno", "declaredAt"));
        Assert.Equal("2026-11-07T09:30:00Z", Text("partner-uno", "verifiedAt"));

        Assert.True(byCode["partner-due"].GetProperty("isDeclared").GetBoolean());
        Assert.Equal("2026-11-04T00:00:00Z", Text("partner-due", "declaredAt"));
        Assert.Null(Text("partner-due", "verifiedAt"));

        Assert.Null(Text("portal-uno", "declaredAt"));
        Assert.Equal("2026-11-02T08:00:00Z", Text("portal-uno", "verifiedAt"));
    }

    [Fact]
    public async Task TheEndpoints_AnswerTheBriefAndTheDeclaration_NoStore_AndAMissingDayIsA422()
    {
        var w = await World.CreateAsync();
        await w.AddPartnerAsync("partner-uno", guideIt: "Scegli il piano", guideEn: "Choose the plan");

        await using var db = w.NewDb();
        var controller = w.Controller(db);

        var brief = Assert.IsType<OkObjectResult>((await controller.GetBrief("partner-uno", w.PropertyId, "en", default)).Result);
        var briefDto = Assert.IsType<MarketplacePartnerBriefDto>(brief.Value);
        Assert.Equal(new[] { "Choose the plan" }, briefDto.Checklist);
        Assert.Equal("Casa Lago", briefDto.PropertyData?.Name);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());

        var missing = await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.Declare("partner-uno", new DeclareMarketplacePartnerRequest { PropertyId = w.PropertyId }, default));
        Assert.Equal("marketplace_declared_date_invalid", missing.Code);
        Assert.Empty(await w.ActivationsAsync());

        var declared = Assert.IsType<OkObjectResult>(
            (await controller.Declare(
                "partner-uno",
                new DeclareMarketplacePartnerRequest { PropertyId = w.PropertyId, DeclaredOn = new DateOnly(2026, 11, 3) },
                default)).Result);
        var dto = Assert.IsType<MarketplacePartnerActivationDto>(declared.Value);
        Assert.Equal("partner-uno", dto.Code);
        Assert.Equal(w.PropertyId, dto.PropertyId);
        Assert.Equal(MarketplaceActivationStatus.Active, dto.Status);
        Assert.True(dto.IsDeclared);
        Assert.Null(dto.VerifiedAt);
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), dto.DeclaredAt);
    }

    [Fact]
    public void TheEndpoints_TheBriefNeedsBookingRead_TheDeclarationNeedsOtaWrite_AndNeitherSitsBehindTheConnector()
    {
        var controller = typeof(MarketplacePartnersController);
        Assert.Equal("api/marketplace/partners", controller.GetCustomAttribute<RouteAttribute>()!.Template);

        // The partners never depend on Octorate; and the policy is on each action, so the brief does not ask for the write permission.
        Assert.Empty(controller.GetCustomAttributes<FeatureGateAttribute>());
        Assert.Empty(controller.GetCustomAttributes<AuthorizeAttribute>());

        string? PolicyOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().Single().Policy;
        HttpMethodAttribute VerbOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single();

        Assert.Equal(CasazenPolicies.BookingRead, PolicyOf(nameof(MarketplacePartnersController.GetBrief)));
        Assert.Equal(CasazenPolicies.OtaWrite, PolicyOf(nameof(MarketplacePartnersController.Declare)));
        Assert.Equal("{code}/brief", VerbOf(nameof(MarketplacePartnersController.GetBrief)).Template);
        Assert.Equal("{code}/declare", VerbOf(nameof(MarketplacePartnersController.Declare)).Template);
        Assert.Equal(new[] { "GET" }, VerbOf(nameof(MarketplacePartnersController.GetBrief)).HttpMethods);
        Assert.Equal(new[] { "POST" }, VerbOf(nameof(MarketplacePartnersController.Declare)).HttpMethods);
    }

    [Fact]
    public void Registration_TheServiceIsScoped()
    {
        var services = new ServiceCollection();
        services.AddCasazenMarketplace(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMarketplacePartnerActivationService));
        Assert.Equal(typeof(MarketplacePartnerActivationService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void TheMessages_AreInBothLanguages()
    {
        var texts = new ResxMarketplaceTexts();
        foreach (var key in new[] { "MarketplaceDeclaredDateInvalid", "MarketplacePartnerNeedsWholeOrg" })
        {
            var italian = texts.Get(key, "it");
            var english = texts.Get(key, "en");

            Assert.NotEqual(key, italian);
            Assert.NotEqual(key, english);
            Assert.NotEqual(italian, english);
        }
    }

    private static MarketplaceCatalogEntry CardOf(MarketplaceCatalog catalog, string code) =>
        catalog.Groups.Single(group => group.Id == MarketplaceGroupIds.Partners).Entries.Single(entry => entry.Code == code);

    private static MarketplaceActivation Row(
        Guid orgId,
        Guid? propertyId,
        MarketplaceActivationKind kind,
        string code,
        MarketplaceActivationStatus status,
        bool declared = false,
        bool verified = false) =>
        new()
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Kind = kind,
            TargetCode = code,
            Status = status,
            IsDeclared = declared,
            DeclaredAt = declared ? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            VerifiedAt = verified ? new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc) : null,
        };

    private static MarketplaceActivation PartnerRow(IEnumerable<MarketplaceActivation> rows) =>
        rows.Single(row => row.Kind == MarketplaceActivationKind.Partner);

    private static string Describe(object entity) =>
        string.Join(
            "; ",
            entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => $"{property.Name}={property.GetValue(entity)}"));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public DateTime Now => _now.UtcDateTime;

        public void Set(DateTimeOffset utcNow) => _now = utcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class World
    {
        public Guid PropertyId { get; private set; }

        public Guid OtherPropertyId { get; private set; }

        public Guid PropertyOfB { get; private set; }

        public HostScope OrgWide => new(OrgA);

        public bool ConnectorOn { get; set; } = true;

        public DbContextOptions<AppDbContext> Options { get; } =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"partner-activation-{Guid.NewGuid()}").Options;

        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero));

        public CapturingLogger<MarketplacePartnerActivationService> Logs { get; } = new();

        public static async Task<World> CreateAsync(bool connected = true)
        {
            var world = new World();
            await world.SeedAsync(connected);
            return world;
        }

        public AppDbContext NewDb() => new(Options);

        public MarketplacePartnerActivationService Service(AppDbContext db) => new(db, Flags(), HostEnvironment(), Clock, Logs);

        public MarketplaceCatalogService Catalog(AppDbContext db)
        {
            var suppliers = new Mock<IHostSupplierUsageReader>();
            suppliers
                .Setup(s => s.ListAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<HostUsedSupplier>());
            return new MarketplaceCatalogService(db, suppliers.Object, Flags(), Mock.Of<IFileStorage>(), new ResxMarketplaceTexts(), HostEnvironment());
        }

        public MarketplacePartnersController Controller(AppDbContext db)
        {
            var orgs = new Mock<IOrgContextResolver>();
            orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgA);
            var scopes = new Mock<IHostScopeResolver>();
            scopes
                .Setup(s => s.ResolveAsync(Host, It.IsAny<IReadOnlySet<string>>(), OrgA, It.IsAny<CancellationToken>()))
                .ReturnsAsync(OrgWide);
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Host)], "test")) };
            return new MarketplacePartnersController(Service(db), orgs.Object, scopes.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        public async Task GrantAsync(string userId, Guid propertyId)
        {
            await using var db = NewDb();
            db.Set<PropertyMemberAccess>().Add(new PropertyMemberAccess { OrgId = OrgA, UserId = userId, PropertyId = propertyId });
            await db.SaveChangesAsync();
        }

        public async Task AddPartnerAsync(
            string code,
            bool visible = true,
            PlanTier[]? plans = null,
            string[]? countries = null,
            string? portalCode = null,
            string? guideIt = "Scegli il piano\nAutorizza l'accesso",
            string? guideEn = null)
        {
            var partner = new MarketplacePartner
            {
                Code = code,
                Name = "Partner " + code,
                IsVisible = visible,
                WebsiteUrl = "https://example.com/" + code,
                OctoratePortalCode = portalCode,
                VisiblePlanTiers = plans ?? [],
                VisibleCountries = [.. countries ?? []],
            };
            if (guideIt is not null)
                partner.Texts.Add(Text(partner.Id, MarketplacePartnerText.Italian, guideIt));

            if (guideEn is not null)
                partner.Texts.Add(Text(partner.Id, MarketplacePartnerText.English, guideEn));

            await using var db = NewDb();
            db.MarketplacePartners.Add(partner);
            await db.SaveChangesAsync();
        }

        public async Task HidePartnerAsync(string code, bool visible = false)
        {
            await using var db = NewDb();
            (await db.MarketplacePartners.SingleAsync(p => p.Code == code)).IsVisible = visible;
            await db.SaveChangesAsync();
        }

        public async Task SetCinAsync(string? cin)
        {
            await using var db = NewDb();
            (await db.Properties.SingleAsync(p => p.Id == PropertyId)).CinCode = cin;
            await db.SaveChangesAsync();
        }

        public async Task AddStayAsync()
        {
            var guest = new Guest
            {
                OrgId = OrgA,
                FirstName = "Mario-TEST",
                LastName = "Rossi-TEST",
                Email = "mario.rossi.test@example.com",
                PhoneNumber = "+39 333 0000000",
                DocumentNumber = "DOCUMENTO-DI-TEST-123",
            };
            var booking = new Booking
            {
                OrgId = OrgA,
                PropertyId = PropertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 12, 4, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                BasePrice = 1111.11m,
                TotalPrice = 1234.56m,
                SpecialRequests = "RICHIESTA-RISERVATA",
                BookingCode = "BK-TEST-0001",
                ExternalId = "EXT-TEST-0001",
            };
            await using var db = NewDb();
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        }

        public async Task AddRowAsync(MarketplaceActivation row)
        {
            await using var db = NewDb();
            db.MarketplaceActivations.Add(row);
            await db.SaveChangesAsync();
        }

        public async Task<List<MarketplaceActivation>> ActivationsAsync(Guid? orgId = null)
        {
            await using var db = NewDb();
            var org = orgId ?? OrgA;
            return await db.MarketplaceActivations.AsNoTracking().Where(a => a.OrgId == org).OrderBy(a => a.PropertyId).ToListAsync();
        }

        public async Task<List<string>> RowsAsync(Guid? orgId = null) => (await ActivationsAsync(orgId)).Select(Describe).ToList();

        public async Task SeedOtherOrgAsync()
        {
            var property = new Property { OrgId = OrgB, OwnerId = "auth0|owner-b", Name = "Casa B", Address = "Via B 1", City = "Roma", MaxGuests = 2 };
            PropertyOfB = property.Id;

            await using var db = NewDb();
            db.Orgs.Add(new OrgEntity { Id = OrgB, Name = "Org B", Slug = "org-b", DisplayName = "Org B", PlanTier = PlanTier.Pro, BillingCountry = "IT" });
            db.Properties.Add(property);
            await db.SaveChangesAsync();
        }

        private static MarketplacePartnerText Text(Guid partnerId, string language, string guide) =>
            new()
            {
                PartnerId = partnerId,
                Language = language,
                Benefit = "Beneficio",
                Audience = "Per tutti",
                Description = "Descrizione",
                Requirements = "Primo requisito",
                DataShared = "Nessun dato",
                Deactivation = "Si disattiva sul sito del partner",
                ActivationGuide = guide,
            };

        private async Task SeedAsync(bool connected)
        {
            var first = NewProperty("Casa Lago");
            var second = NewProperty("Casa Monte");
            PropertyId = first.Id;
            OtherPropertyId = second.Id;

            await using var db = NewDb();
            db.Orgs.Add(new OrgEntity { Id = OrgA, Name = "Org A", Slug = "org-a", DisplayName = "Org A", PlanTier = PlanTier.Pro, BillingCountry = "IT" });
            db.OctorateConnections.Add(new OctorateConnection
            {
                OrgId = OrgA,
                Environment = OctorateEnvironment.Test,
                Status = connected ? OctorateConnectionStatus.Connected : OctorateConnectionStatus.Disconnected,
            });
            db.Properties.AddRange(first, second);
            await db.SaveChangesAsync();
        }

        private static Property NewProperty(string name) =>
            new()
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner",
                Name = name,
                Address = "Via Prova 1",
                Unit = "int. 9",
                City = "Como",
                PostalCode = "22100",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 4,
                CinCode = ValidCin,
                Description = "DESCRIZIONE-RISERVATA",
                HouseRules = "REGOLE-RISERVATE",
                NightlyRate = 199.99m,
                CleaningFee = 77.77m,
                TaxpayerFiscalCode = "FISCALE-RISERVATO",
            };

        private IFeatureFlags Flags()
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(() => ConnectorOn);
            return flags.Object;
        }

        private static IHostEnvironment HostEnvironment()
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            return host.Object;
        }
    }
}
