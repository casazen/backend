using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceEntryDetailService"/> (E09/F01/US01/T04, NF-55): the detail card of a channel, a tool, a function of the license or a
/// partner. For a channel or a tool what is needed to activate it comes from the flags of the portal in the daily cache, and the other words
/// from the resource files with the name of the portal put in; for a function of the license from the resource files by module code; for a
/// partner from its texts in the catalog (Italian when the language asked for has none). Nothing is invented: an unknown cost is <c>null</c>.
/// </summary>
/// <remarks>
/// A code that does not exist, a partner that is hidden or that the plan or the country of the org does not allow, an entry of another kind than
/// its code, and an entry that goes through Octorate while the connector is off are the same <c>marketplace_entry_not_found</c> (404).
/// </remarks>
public sealed class MarketplaceEntryDetailService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IFileStorage storage,
    IMarketplaceTexts texts,
    IHostEnvironment hostEnvironment) : IMarketplaceEntryDetailService
{
    /// <summary>The questions of the FAQ of a channel, a tool or a function of the license: <c>Faq1</c> to <c>Faq3</c> in the resource files.</summary>
    private const int ResourceFaqCount = 3;

    public async Task<MarketplaceEntryDetail> GetAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationKind kind,
        string code,
        Guid? propertyId,
        string language,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);
        var wanted = code?.Trim();
        if (string.IsNullOrEmpty(wanted) || !Enum.IsDefined(kind))
            throw MarketplaceReads.EntryNotFound();

        // What goes through Octorate does not exist for anybody while the connector is off; only the partners are not behind the flag.
        if (kind != MarketplaceActivationKind.Partner && !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            throw MarketplaceReads.EntryNotFound();

        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, propertyId, ct);
        var org = await MarketplaceReads.LoadOrgAsync(db, orgId, ct);
        var octorate = await MarketplaceReads.OctorateStateAsync(db, featureFlags, hostEnvironment, orgId, ct);
        var states = await MarketplaceCardStates.LoadAsync(db, orgId, scope, propertyId, ct);

        return kind switch
        {
            MarketplaceActivationKind.Partner => await PartnerAsync(wanted, org, lang, states, ct),
            MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool => await PortalAsync(kind, wanted, lang, octorate, states, ct),
            _ => LicensedFunction(wanted, lang, octorate, states),
        };
    }

    private async Task<MarketplaceEntryDetail> PartnerAsync(
        string code, MarketplaceReads.OrgFacts org, string lang, MarketplaceCardStates states, CancellationToken ct)
    {
        // Hidden, or not for this plan or country: the answer is the one of a code nobody has.
        var partner = await db.MarketplacePartners.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
        if (partner is null || !MarketplaceVisibility.IsVisible(partner, org.PlanTier, org.BillingCountry))
            throw MarketplaceReads.EntryNotFound();

        var partnerTexts = await db.MarketplacePartnerTexts.AsNoTracking().Where(t => t.PartnerId == partner.Id).ToListAsync(ct);
        var text = MarketplaceReads.TextOf(partnerTexts, lang);
        var state = states.Of(MarketplaceActivationKind.Partner, partner.Code);

        // The activation guide belongs to the hand-off wizard, not to the card. The host activates on the partner's site and deactivates there too.
        return new MarketplaceEntryDetail(
            MarketplaceActivationKind.Partner,
            partner.Code,
            partner.Name,
            MarketplaceReads.PartnerLogoUrl(storage, partner),
            state.Status,
            text?.Description ?? string.Empty,
            text?.Audience ?? string.Empty,
            new MarketplaceActivationInfo(
                MarketplaceActivationTypes.PartnerSite,
                [.. Lines(text?.Requirements).Select((line, index) => new MarketplaceRequirement($"requirement_{index + 1}", line))],
                false),
            new MarketplaceCost(text?.CostText, partner.InvoicedBy),
            Lines(text?.DataShared),
            new MarketplaceDeactivation(text?.Deactivation ?? string.Empty, true),
            ParseFaq(text?.FaqJson),
            MarketplaceCardRules.ActionOf(MarketplaceActivationKind.Partner, state.Status, octorateConnected: false),
            partner.WebsiteUrl);
    }

    private async Task<MarketplaceEntryDetail> PortalAsync(
        MarketplaceActivationKind kind,
        string code,
        string lang,
        MarketplaceOctorateState octorate,
        MarketplaceCardStates states,
        CancellationToken ct)
    {
        var category = kind == MarketplaceActivationKind.Channel ? MarketplacePortalCategory.Ota : MarketplacePortalCategory.ExternalTool;
        var portal = await db.MarketplacePortals.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PortalCode == code && p.IsListed && p.Enabled, ct);
        if (portal is null || portal.Category != category)
            throw MarketplaceReads.EntryNotFound();

        // What the connection needs, from the flags of the portal: the identifier on the channel, its username and password, the consent
        // given on the channel manager's own panel.
        var requirements = new List<MarketplaceRequirement>();
        if (portal.RequiresHotelId)
            requirements.Add(Requirement("hotelId", lang));

        if (portal.RequiresPassword)
            requirements.Add(Requirement("credentials", lang));

        if (portal.RequiresTokenAuthentication)
            requirements.Add(Requirement("panelAccess", lang));

        var state = states.Of(kind, portal.PortalCode);
        var segment = kind.ToString();
        var whatItDoes = portal.FeatureContent ? "WhatItDoesWithContent" : "WhatItDoes";
        return new MarketplaceEntryDetail(
            kind,
            portal.PortalCode,
            portal.Name,
            portal.LogoUrl,
            state.Status,
            texts.Get($"Marketplace.Detail.{segment}.{whatItDoes}", lang, portal.Name),
            texts.Get($"Marketplace.Detail.{segment}.Audience", lang, portal.Name),
            new MarketplaceActivationInfo(MarketplaceActivationTypes.Api, requirements, portal.RequiresTokenAuthentication),
            MarketplaceCost.Unknown,
            Lines(texts.Get($"Marketplace.Detail.{segment}.DataShared", lang, portal.Name)),
            new MarketplaceDeactivation(texts.Get($"Marketplace.Detail.{segment}.Deactivation", lang, portal.Name), false),
            ResourceFaq(segment, lang, portal.Name),
            MarketplaceCardRules.ActionOf(kind, state.Status, octorate.Connected, portal.Creatable),
            null);
    }

    private MarketplaceEntryDetail LicensedFunction(string code, string lang, MarketplaceOctorateState octorate, MarketplaceCardStates states)
    {
        if (!MarketplaceLicensedFunctions.IsKnown(code))
            throw MarketplaceReads.EntryNotFound();

        var state = states.Of(MarketplaceActivationKind.LicensedFunction, code);
        const string Segment = nameof(MarketplaceActivationKind.LicensedFunction);

        // The cost is never invented: it depends on the license the host has with its channel manager, and the page says it is not specified.
        return new MarketplaceEntryDetail(
            MarketplaceActivationKind.LicensedFunction,
            code,
            texts.Get($"Marketplace.Licensed.{code}.Name", lang),
            null,
            state.Status,
            texts.Get($"Marketplace.Detail.{Segment}.WhatItDoes.{code}", lang),
            texts.Get($"Marketplace.Detail.{Segment}.Audience", lang),
            new MarketplaceActivationInfo(MarketplaceActivationTypes.LicenseRequest, [Requirement("licenseRequest", lang)], false),
            MarketplaceCost.Unknown,
            Lines(texts.Get($"Marketplace.Detail.{Segment}.DataShared", lang)),
            new MarketplaceDeactivation(texts.Get($"Marketplace.Detail.{Segment}.Deactivation", lang), false),
            ResourceFaq(Segment, lang),
            MarketplaceCardRules.ActionOf(MarketplaceActivationKind.LicensedFunction, state.Status, octorate.Connected),
            null);
    }

    private MarketplaceRequirement Requirement(string code, string lang) =>
        new(code, texts.Get($"Marketplace.Detail.Requirement.{code}", lang));

    /// <summary>The questions and answers of a kind from the resource files, <c>Faq1.Q</c> and <c>Faq1.A</c> to <c>Faq3</c>, with the portal name put in when there is one.</summary>
    private List<MarketplaceFaq> ResourceFaq(string segment, string lang, params object[] args)
    {
        var faq = new List<MarketplaceFaq>();
        for (var number = 1; number <= ResourceFaqCount; number++)
        {
            faq.Add(new MarketplaceFaq(
                texts.Get($"Marketplace.Detail.{segment}.Faq{number}.Q", lang, args),
                texts.Get($"Marketplace.Detail.{segment}.Faq{number}.A", lang, args)));
        }

        return faq;
    }

    /// <summary>The lines of a text, trimmed, without the empty ones: how a list of plain sentences is stored in one resource or column.</summary>
    private static List<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    /// <summary>
    /// The questions of a partner, a JSON array of <c>{ "q", "a" }</c>. An item that is not an object with both texts is left out, and a column
    /// that is not that array gives an empty list: the card shows no FAQ rather than a broken one.
    /// </summary>
    private static List<MarketplaceFaq> ParseFaq(string? json)
    {
        var faq = new List<MarketplaceFaq>();
        if (string.IsNullOrWhiteSpace(json))
            return faq;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return faq;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var question = TextOf(item, "q");
                var answer = TextOf(item, "a");
                if (question is not null && answer is not null)
                    faq.Add(new MarketplaceFaq(question, answer));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return faq;
    }

    private static string? TextOf(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
