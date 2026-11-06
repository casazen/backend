using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02/T02: the templates of the host and the read-only templates of the channel manager, on the real service over a database. The proof the brief
/// asks for: a template of one organization is never listed, changed, deleted or rendered for another. The rest is what makes a template safe to put in
/// front of a guest: it is checked when it is saved, a conversation with no value for a variable is refused with the name of the variable, and a conversation
/// the caller does not reach is a 404.
/// </summary>
public class MessageTemplateProofTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Mock<IOctorateApiClient> _client = new();
    private readonly TestFeatureFlags _flags = new(FeatureFlags.OctorateConnector);
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private MessageTemplateService Service() => new(
        _db,
        _client.Object,
        new OctorateOrgScope(NullTenantContext.Instance),
        _flags,
        NullLogger<MessageTemplateService>.Instance,
        new FixedTimeProvider(MessageHubTestKit.Now));

    private HostScope Other => new(_world.Scope.OtherOrgId);

    private static MessageThreadKey Ota(OtaMessageThread thread) => MessageThreadKey.ForOta(thread.Id);

    // ---- Tenant data ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (tenant). A template of organization A is never listed, changed, deleted or rendered for organization B: it is a 404
    /// <c>message_template_not_found</c>, the same as one that does not exist, and the template is left as it was.
    /// </summary>
    [Fact]
    public async Task MessageTemplate_OtherOrg_NotVisible_Test()
    {
        var service = Service();
        var mine = await service.CreateAsync(_world.Scope.OrgWide, "Benvenuto", "it", "Ciao {{guest_name}}", true);
        var theirs = await service.CreateAsync(Other, "Benvenuto", "it", "Salve {{guest_name}}", true);

        Assert.Equal(mine.Name, theirs.Name);
        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal([mine.Id], (await service.ListAsync(_world.Scope.OrgWide, activeOnly: false)).Select(t => t.Id));
        Assert.Equal([theirs.Id], (await service.ListAsync(Other, activeOnly: false)).Select(t => t.Id));

        // Org B reaches for the template of org A by its id: every door answers as if it were not there.
        var update = await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(Other, mine.Id, "Rubato", "it", "Testo", true));
        var delete = await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(Other, mine.Id));
        var render = await Assert.ThrowsAsync<NotFoundException>(() => service.RenderAsync(Other, mine.Id, Ota(_world.OtherOrg)));
        Assert.All(new[] { update, delete, render }, error => Assert.Equal("message_template_not_found", error.Code));

        var untouched = await _db.MessageTemplates.AsNoTracking().SingleAsync(t => t.Id == mine.Id);
        Assert.Equal("Benvenuto", untouched.Name);
        Assert.Equal("Ciao {{guest_name}}", untouched.Body);
        Assert.Equal(_world.Scope.OrgId, untouched.OrgId);

        // And the conversation of another org is not rendered for, even with a template of one's own.
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => service.RenderAsync(_world.Scope.OrgWide, mine.Id, Ota(_world.OtherOrg)));
        Assert.Equal("thread_not_found", foreign.Code);
    }

    // ---- Writing a template ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ATemplate_IsCheckedWhenItIsSaved()
    {
        var service = Service();

        var created = await service.CreateAsync(_world.Scope.OrgWide, "  Check-in  ", " IT ", "Ciao {{ guest_name }}, check-in {{check_in_time}}", true);

        Assert.Equal("Check-in", created.Name);
        Assert.Equal("it", created.Language);
        Assert.Equal(["guest_name", "check_in_time"], created.Variables);
        Assert.True(created.IsActive);

        foreach (var (name, language, body) in new (string?, string?, string?)[]
                 {
                     (null, "it", "Testo"),
                     ("   ", "it", "Testo"),
                     (new string('n', MessageTemplate.NameMaxLength + 1), "it", "Testo"),
                     ("Nome", "xx", "Testo"),
                     ("Nome", null, "Testo"),
                     ("Nome", "it", null),
                     ("Nome", "it", "   "),
                     ("Nome", "it", new string('b', MessageTemplate.BodyMaxLength + 1)),
                 })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateAsync(_world.Scope.OrgWide, name, language, body, true));
            Assert.Equal("message_template_invalid", invalid.Code);
        }

        // A variable that is none of the eight is found now, not in front of a guest.
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateAsync(_world.Scope.OrgWide, "Nome", "it", "Ciao {{nome_ospite}}", true));
        Assert.Equal("template_variable_unknown", unknown.Code);
        Assert.Single(await _db.MessageTemplates.AsNoTracking().Where(t => t.OrgId == _world.Scope.OrgId).ToListAsync());
    }

    [Fact]
    public async Task Create_AndUpdate_TheSameNameAndLanguageTwice_Is409_AnotherLanguageIsFine()
    {
        var service = Service();
        var first = await service.CreateAsync(_world.Scope.OrgWide, "Benvenuto", "it", "Ciao", true);
        var english = await service.CreateAsync(_world.Scope.OrgWide, "Benvenuto", "en", "Hello", true);

        var duplicate = await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateAsync(_world.Scope.OrgWide, "Benvenuto", "it", "Altro", true));
        var clash = await Assert.ThrowsAsync<DomainConflictException>(() => service.UpdateAsync(_world.Scope.OrgWide, english.Id, "Benvenuto", "it", "Hello", true));

        Assert.Equal("message_template_exists", duplicate.Code);
        Assert.Equal("message_template_exists", clash.Code);

        // A template may be saved again as itself.
        var same = await service.UpdateAsync(_world.Scope.OrgWide, first.Id, "Benvenuto", "it", "Ciao, benvenuto!", false);
        Assert.Equal("Ciao, benvenuto!", same.Body);
        Assert.False(same.IsActive);
    }

    [Fact]
    public async Task List_ActiveOnly_KeepsTheTemplatesTheComposerOffers()
    {
        var service = Service();
        await service.CreateAsync(_world.Scope.OrgWide, "Attivo", "it", "Ciao", true);
        await service.CreateAsync(_world.Scope.OrgWide, "Spento", "it", "Ciao", false);

        Assert.Equal(["Attivo", "Spento"], (await service.ListAsync(_world.Scope.OrgWide, activeOnly: false)).Select(t => t.Name));
        Assert.Equal(["Attivo"], (await service.ListAsync(_world.Scope.OrgWide, activeOnly: true)).Select(t => t.Name));
    }

    [Fact]
    public async Task Delete_ATemplate_RemovesIt()
    {
        var service = Service();
        var template = await service.CreateAsync(_world.Scope.OrgWide, "Benvenuto", "it", "Ciao", true);

        await service.DeleteAsync(_world.Scope.OrgWide, template.Id);

        Assert.Empty(await service.ListAsync(_world.Scope.OrgWide, activeOnly: false));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(_world.Scope.OrgWide, template.Id));
    }

    // ---- Rendering for a conversation ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Render_FillsTheVariablesFromTheBookingAndTheProperty_OfTheConversation()
    {
        var granted = await _db.Properties.FirstAsync(p => p.Id == _world.Scope.Granted.Id);
        granted.CheckInFrom = new TimeOnly(15, 0);
        granted.CheckOutBy = new TimeOnly(10, 30);
        var org = await _db.Orgs.FirstAsync(o => o.Id == _world.Scope.OrgId);
        org.HostName = "Giulia";
        await _db.SaveChangesAsync();
        var booking = await _db.Bookings.AsNoTracking().FirstAsync(b => b.Id == _world.GrantedChannelBooking.Id);
        var service = Service();
        var template = await service.CreateAsync(
            _world.Scope.OrgWide,
            "Arrivo",
            "en",
            "Hi {{guest_name}}, welcome to {{property_name}} ({{booking_code}}). Check-in {{check_in_date}} from {{check_in_time}}, check-out {{check_out_date}} by {{check_out_time}}. {{host_name}}",
            true);

        var text = await service.RenderAsync(_world.Scope.OrgWide, template.Id, Ota(_world.GrantedReservation));

        Assert.StartsWith("Hi Jonas, welcome to Trullo (", text, StringComparison.Ordinal);
        Assert.Contains(BookingCodes.Format(booking.BookingCode), text, StringComparison.Ordinal);
        Assert.Contains(" from 15:00, ", text, StringComparison.Ordinal);
        Assert.Contains(" by 10:30. Giulia", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }

    /// <summary>A request with no booking has no dates: the text is refused with the name of the variable, not sent with a literal brace in it.</summary>
    [Fact]
    public async Task Render_AConversationWithNoValueForAVariable_Is422NamingIt()
    {
        var service = Service();
        var withDate = await service.CreateAsync(_world.Scope.OrgWide, "Con data", "it", "Arrivo il {{check_in_date}}", true);
        var withProperty = await service.CreateAsync(_world.Scope.OrgWide, "Con casa", "it", "Benvenuti al {{property_name}}", true);

        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => service.RenderAsync(_world.Scope.OrgWide, withDate.Id, Ota(_world.GrantedRequest)));

        Assert.Equal("template_variable_missing", missing.Code);
        Assert.Equal("check_in_date", Assert.Single(missing.MessageArgs));
        Assert.Equal("Benvenuti al Trullo", await service.RenderAsync(_world.Scope.OrgWide, withProperty.Id, Ota(_world.GrantedRequest)));
    }

    [Fact]
    public async Task Render_ForAConversationWithTheGuestOfADirectBooking_UsesItsStay()
    {
        var service = Service();
        var template = await service.CreateAsync(_world.Scope.OrgWide, "Arrivo", "it", "{{guest_name}}, ti aspettiamo il {{check_in_date}}", true);

        var text = await service.RenderAsync(_world.Scope.OrgWide, template.Id, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id));

        Assert.StartsWith("Laura, ti aspettiamo il ", text, StringComparison.Ordinal);
        Assert.Contains("2026", text, StringComparison.Ordinal);
    }

    /// <summary>A collaborator limited to some houses renders for the conversations of its houses and for no other, and nothing is read of the others.</summary>
    [Fact]
    public async Task Render_Collaborator_ReachesOnlyTheConversationsOfItsHouses()
    {
        var service = Service();
        var template = await service.CreateAsync(_world.Scope.OrgWide, "Casa", "it", "Benvenuti al {{property_name}}", true);
        var collaborator = _world.Scope.Restricted;

        Assert.Equal("Benvenuti al Trullo", await service.RenderAsync(collaborator, template.Id, Ota(_world.GrantedReservation)));
        foreach (var key in new[] { Ota(_world.HiddenReservation), Ota(_world.Unassigned), MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id) })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => service.RenderAsync(collaborator, template.Id, key));
            Assert.Equal("thread_not_found", error.Code);
        }
    }

    // ---- The templates of the channel manager ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChannelTemplates_OfAChannelConversation_AreTheOnesOfTheChannelManager_ReadOnly()
    {
        _client
            .Setup(c => c.GetChatTemplatesAsync("ACC-1", _world.GrantedReservation.OctorateThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChatTemplate("11", "Conferma", "IT", "Grazie per la prenotazione"), new OctorateChatTemplate("12", "Indicazioni", null, "Da casa...")]);

        var items = await Service().ChannelTemplatesAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation));

        Assert.Equal(["Conferma", "Indicazioni"], items.Select(i => i.Name));
        Assert.Equal("Grazie per la prenotazione", items[0].Text);
        Assert.Equal("IT", items[0].Language);
        Assert.Null(items[1].Language);
        Assert.Empty(await _db.MessageTemplates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ChannelTemplates_ADirectConversation_HasNone_AndTheChannelManagerIsNotAsked()
    {
        var items = await Service().ChannelTemplatesAsync(_world.Scope.OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id));

        Assert.Empty(items);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChannelTemplates_AConversationOutOfReach_IsA404_AndTheConnectorOffIsA409()
    {
        var service = Service();

        var hidden = await Assert.ThrowsAsync<NotFoundException>(() => service.ChannelTemplatesAsync(_world.Scope.Restricted, Ota(_world.HiddenReservation)));
        Assert.Equal("thread_not_found", hidden.Code);

        _flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await Assert.ThrowsAsync<OctorateAuthException>(() => service.ChannelTemplatesAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation)));
        Assert.Equal(OctorateAuthException.NotConnectedCode, off.Code);
        _client.VerifyNoOtherCalls();
    }
}
