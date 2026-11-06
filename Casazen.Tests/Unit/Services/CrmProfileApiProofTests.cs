using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Crm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F01/US03/T01 (NF-88, AC-NF88-4 to AC-NF88-6): the proofs of the profile API of the CRM, the read model behind <c>GET api/crm/guests</c>,
/// <c>GET api/crm/guests/{id}</c> and <c>PUT api/crm/guests/{id}/tags</c>. A collaborator limited to some properties sees only the guests of
/// those properties, with figures computed over their bookings; a profile of another org is as good as missing; and nothing of the registration
/// of a guest (document, birth, place of birth, citizenship, notes) reaches the answer. On EF InMemory, the real service over the real keyed index.
/// </summary>
public class CrmProfileApiProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly CrmProfileFilter NoFilter = new();

    /// <summary>
    /// Permission rule: a collaborator limited to some properties never sees the guest of another property. One person stayed at the house the
    /// collaborator was given and, with another guest record, at one it was not: the profile is shown, with the stays, the spend and the channel of
    /// the first house only (not the totals stored on the profile, which are the whole org's), the name of the record it reaches and none of the
    /// other, in the list, in the search, in the detail and in the link to the guest record. The guest of the other house only, a lead of the welcome
    /// page and a guest of another org are not there; a collaborator with no property gets nothing, and the whole org sees what it should.
    /// </summary>
    [Fact]
    public async Task CrmProfiles_ScopedCaller_SeesOnlyProfilesOfItsProperties_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        Guid sharedId, rivalId, leadId, atGrantedId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var granted = CrmProfileApiWorld.AddHouse(seed, orgId, "Trullo");
            var hidden = CrmProfileApiWorld.AddHouse(seed, orgId, "Casa Bianca");
            seed.PropertyMemberAccesses.Add(new PropertyMemberAccess
            {
                OrgId = orgId,
                UserId = CrmProfileApiWorld.Collaborator,
                PropertyId = granted.Id,
            });

            // One person, two guest records: Anna Rossi at the house of the collaborator, Annamaria Segreta (the primary guest of the profile) at the other.
            var atGranted = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Rossi", "anna.rossi@example.com");
            var atHidden = CrmTestWorld.AddGuest(seed, orgId, "Annamaria", "Segreta", "anna.segreta@example.com");
            CrmTestWorld.AddBooking(seed, atGranted, granted.Id, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
            CrmTestWorld.AddBooking(seed, atHidden, hidden.Id, BookingStatus.CheckedOut, BookingSource.Airbnb, -60, 3, 300m);
            var shared = CrmProfileApiWorld.NewProfile(orgId, atHidden.Id, stays: 2, spend: 500m);
            atGranted.GuestCrmProfileId = shared.Id;
            atHidden.GuestCrmProfileId = shared.Id;
            seed.GuestCrmProfiles.Add(shared);

            // Another person, who stayed at the other house only; a lead with no guest; a guest of another org.
            var rival = CrmProfileApiWorld.AddPerson(seed, orgId, "Bruno", "Verdi", hidden);
            var lead = CrmProfileApiWorld.NewProfile(orgId, null, stays: 0, spend: 0m);
            seed.GuestCrmProfiles.Add(lead);
            var leadContact = CrmProfileApiWorld.NewContact(
                orgId, lead.Id, GuestContactChannel.Email, "lea.gialli@example.com", GuestContactOrigin.WelcomePage, GuestContactLegalBasis.None);
            leadContact.DisplayName = "Lea Gialli";
            seed.GuestContacts.Add(leadContact);
            CrmProfileApiWorld.AddPerson(seed, otherOrgId, "Carla", "Neri");
            await seed.SaveChangesAsync(Ct);

            sharedId = shared.Id;
            rivalId = rival.Profile.Id;
            leadId = lead.Id;
            atGrantedId = atGranted.Id;
        }

        var restricted = new HostScope(orgId, GrantedToUserId: CrmProfileApiWorld.Collaborator);
        var orgWide = new HostScope(orgId);
        var noProperty = new HostScope(orgId, GrantedToUserId: "auth0|senza-strutture");
        await using var db = CrmTestWorld.Open(store);
        var service = CrmProfileApiWorld.ReadService(db, clock);

        // The list: the person, with what happened at the house of the collaborator.
        var limited = await service.ListAsync(restricted, NoFilter, 1, 50, Ct);
        var item = Assert.Single(limited.Items);
        Assert.Equal(1, limited.TotalCount);
        Assert.Equal(sharedId, item.Id);
        Assert.Equal("Anna Rossi", item.DisplayName);
        Assert.Equal(1, item.TotalStays);
        Assert.Equal(200m, item.TotalSpend);
        Assert.Equal("Direct", Assert.Single(item.Channels));

        // The whole org sees the three profiles of the org (not the one of the other org), with the totals stored on the profile.
        var whole = await service.ListAsync(orgWide, NoFilter, 1, 50, Ct);
        Assert.Equal(3, whole.TotalCount);
        Assert.True(whole.Items.Select(i => i.Id).ToHashSet().SetEquals([sharedId, rivalId, leadId]));
        var wholeShared = whole.Items.Single(i => i.Id == sharedId);
        Assert.Equal("Annamaria Segreta", wholeShared.DisplayName);
        Assert.Equal(2, wholeShared.TotalStays);
        Assert.Equal(500m, wholeShared.TotalSpend);
        var wholeLead = whole.Items.Single(i => i.Id == leadId);
        Assert.Equal("Lea Gialli", wholeLead.DisplayName);
        Assert.Equal("l***@example.com", wholeLead.EmailMasked);
        Assert.Equal(GuestContactLegalBasis.None, wholeLead.EmailBasis);

        // A collaborator with no property is never widened to the org.
        var none = await service.ListAsync(noProperty, NoFilter, 1, 50, Ct);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);

        // The search finds the name of a guest the collaborator reaches and nothing of the others.
        Assert.Single((await service.ListAsync(restricted, new CrmProfileFilter(Search: "anna ross"), 1, 50, Ct)).Items);
        Assert.Empty((await service.ListAsync(restricted, new CrmProfileFilter(Search: "Segreta"), 1, 50, Ct)).Items);
        Assert.Empty((await service.ListAsync(restricted, new CrmProfileFilter(Search: "Bruno"), 1, 50, Ct)).Items);
        Assert.Single((await service.ListAsync(orgWide, new CrmProfileFilter(Search: "Segreta"), 1, 50, Ct)).Items);

        // The detail: the stay of the house of the collaborator, the link to the record it reaches; the others are not found.
        var detail = await service.GetAsync(restricted, sharedId, Ct);
        Assert.Equal("Trullo", Assert.Single(detail.Stays).PropertyName);
        Assert.Equal(1, detail.Totals.Stays);
        Assert.Equal(200m, detail.Totals.Spend);
        Assert.Equal("Trullo", Assert.Single(detail.Totals.ByProperty).PropertyName);
        Assert.Equal("Anna Rossi", detail.DisplayName);
        Assert.Equal(atGrantedId, detail.PrimaryGuestId);
        var rivalMissing = await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(restricted, rivalId, Ct));
        Assert.Equal("crm_profile_not_found", rivalMissing.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(restricted, leadId, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(noProperty, sharedId, Ct));
        Assert.Equal(2, (await service.GetAsync(orgWide, sharedId, Ct)).Stays.Count);
    }

    /// <summary>
    /// Permission rule (tenant isolation): a profile of another org is not there, for the list, the detail and the tags, whatever the reach of the
    /// caller in its own org. The tags of the other org's profile are not changed by the attempt; the same call in the right org works, and a list
    /// of tags that breaks the limits is refused (422) and changes nothing.
    /// </summary>
    [Fact]
    public async Task CrmProfiles_ProfileOfAnotherOrg_Is404_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        Guid profileA, profileB;
        await using (var seed = CrmTestWorld.Open(store))
        {
            profileA = CrmProfileApiWorld.AddPerson(seed, orgA, "Anna", "Rossi").Profile.Id;
            profileB = CrmProfileApiWorld.AddPerson(seed, orgB, "Bruno", "Verdi").Profile.Id;
            await seed.SaveChangesAsync(Ct);
        }

        var scopeA = new HostScope(orgA);
        var scopeB = new HostScope(orgB);
        await using var db = CrmTestWorld.Open(store);
        var service = CrmProfileApiWorld.ReadService(db, clock);

        // The list of an org holds its own profiles only.
        Assert.Equal(profileB, Assert.Single((await service.ListAsync(scopeB, NoFilter, 1, 50, Ct)).Items).Id);
        Assert.Equal(profileA, Assert.Single((await service.ListAsync(scopeA, NoFilter, 1, 50, Ct)).Items).Id);

        // The detail and the tags of the other org's profile: 404, in both directions.
        var detailMissing = await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(scopeB, profileA, Ct));
        Assert.Equal("crm_profile_not_found", detailMissing.Code);
        Assert.Equal("CrmProfileNotFound", detailMissing.MessageKey);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(scopeA, profileB, Ct));
        var tagsMissing = await Assert.ThrowsAsync<NotFoundException>(() => service.SetTagsAsync(scopeB, profileA, ["vip"], Ct));
        Assert.Equal("crm_profile_not_found", tagsMissing.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => service.SetTagsAsync(scopeA, profileB, ["vip"], Ct));
        await using (var check = CrmTestWorld.Open(store))
        {
            Assert.All(await check.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct), profile => Assert.Equal("[]", profile.TagsJson));
        }

        // In the right org it works, and the limits of the tags hold.
        await service.SetTagsAsync(scopeA, profileA, ["vip", "famiglia"], Ct);
        Assert.Equal("vip,famiglia", string.Join(',', (await service.GetAsync(scopeA, profileA, Ct)).Tags));
        var duplicate = await Assert.ThrowsAsync<DomainRuleException>(() => service.SetTagsAsync(scopeA, profileA, ["Vip", "vip"], Ct));
        Assert.Equal("crm_tags_invalid", duplicate.Code);
        await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SetTagsAsync(scopeA, profileA, Enumerable.Range(0, CrmProfileRules.MaxTags + 1).Select(n => $"tag{n}").ToList(), Ct));
        await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SetTagsAsync(scopeA, profileA, [new string('x', CrmProfileRules.MaxTagLength + 1)], Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.SetTagsAsync(scopeA, profileA, null, Ct));
        Assert.Equal("vip,famiglia", string.Join(',', (await service.GetAsync(scopeA, profileA, Ct)).Tags));
    }

    /// <summary>
    /// Privacy rule: no DTO of the CRM carries a document, a birth date, a place of birth, a citizenship, a stay guest or an Alloggiati field.
    /// By shape (no property of any of these types is named after them) and by value: a guest whose record is full of them is listed and opened,
    /// and nothing of it is in the JSON the API would answer with.
    /// </summary>
    [Fact]
    public async Task CrmProfileDetail_NeverExposesAlloggiatiOrDocumentFields_Test()
    {
        var types = typeof(CrmProfileDetailDto).Assembly.GetTypes()
            .Where(type => type.Namespace == "Casazen.Web.DTOs.Crm")
            .Concat([
                typeof(CrmProfileListItem),
                typeof(CrmProfileDetail),
                typeof(CrmProfileStay),
                typeof(CrmProfileTotals),
                typeof(CrmContactView),
                typeof(CrmReviewSummary),
                typeof(CrmMergeCandidateView),
                typeof(CrmMergeProfileSummary),
                typeof(CrmMergeContactSummary),
            ])
            .ToList();
        Assert.True(types.Count >= 20, "The scan of the types of the profile API found almost nothing: it proves nothing.");
        var forbidden = new Regex("document|birth|place|nationality|citizenship|passport|stayguest|alloggiati", RegexOptions.IgnoreCase);
        var offending = types
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => $"{type.Name}.{property.Name}"))
            .Where(name => forbidden.IsMatch(name))
            .ToList();
        Assert.True(offending.Count == 0, "A document or registration field is in the CRM API: " + string.Join(", ", offending));

        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid profileId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var (profile, _, guest) = CrmProfileApiWorld.AddPerson(seed, orgId, "Greta", "Bianchi");
            guest.PhoneNumber = "+39 333 1112223";
            guest.DateOfBirth = new DateTime(1971, 3, 9, 0, 0, 0, DateTimeKind.Utc);
            guest.PlaceOfBirth = "Reykjavik";
            guest.Nationality = "Islandese";
            guest.DocumentType = GuestDocumentType.Passport;
            guest.DocumentNumber = "DOC-SEGRETO-4711";
            guest.DocumentIssuingCountry = "Atlantide";
            guest.Notes = "NOTA-PRIVATA";
            seed.GuestContacts.Add(CrmProfileApiWorld.NewContact(
                orgId, profile.Id, GuestContactChannel.Email, "greta.bianchi@example.com", GuestContactOrigin.DirectBooking, GuestContactLegalBasis.ServiceOnly, guestId: guest.Id));
            await seed.SaveChangesAsync(Ct);
            profileId = profile.Id;
        }

        await using var db = CrmTestWorld.Open(store);
        var service = CrmProfileApiWorld.ReadService(db, clock);
        var scope = new HostScope(orgId);
        var detail = CrmProfileDetailDto.From(await service.GetAsync(scope, profileId, Ct));
        var list = (await service.ListAsync(scope, NoFilter, 1, 50, Ct)).Items.Select(CrmProfileListItemDto.From).ToList();

        var json = JsonSerializer.Serialize(new { detail, list }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("Greta Bianchi", json, StringComparison.Ordinal);
        Assert.Contains("greta.bianchi@example.com", json, StringComparison.Ordinal);
        foreach (var secret in new[] { "DOC-SEGRETO-4711", "Reykjavik", "Islandese", "Atlantide", "NOTA-PRIVATA", "1971-03-09", "Passport", "333 1112223" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The conditions the list runs in SQL (the stays that happened, the e-mail contacts that may receive marketing) are written twice, once as
    /// a function and once over the columns: they must say the same for every combination, or the filter and the screen would disagree about who
    /// may be written to.
    /// </summary>
    [Fact]
    public void CrmRules_ConditionsRunInSql_AgreeWithTheRulesTheyTwin_Test()
    {
        var today = CrmTestWorld.Day(0);
        var stayOf = GuestMatchRules.StayOf(today).Compile();
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            foreach (var offset in new[] { -3, -1, 0, 1, 3 })
            {
                var booking = new Booking { Status = status, CheckOutDate = today.AddDays(offset) };
                Assert.Equal(GuestMatchRules.IsStay(status, booking.CheckOutDate, today), stayOf(booking));
            }
        }

        var marketable = CrmContactRules.MarketableEmail.Compile();
        foreach (var origin in Enum.GetValues<GuestContactOrigin>())
        {
            foreach (var basis in Enum.GetValues<GuestContactLegalBasis>())
            {
                foreach (var confirmation in Enum.GetValues<GuestContactConfirmation>())
                {
                    foreach (var suppressed in new[] { false, true })
                    {
                        var contact = new GuestContact
                        {
                            Channel = GuestContactChannel.Email,
                            Origin = origin,
                            LegalBasis = basis,
                            Confirmation = confirmation,
                            IsSuppressed = suppressed,
                        };
                        Assert.Equal(CrmContactRules.CanReceiveMarketing(contact), marketable(contact));
                    }
                }
            }
        }
    }
}
