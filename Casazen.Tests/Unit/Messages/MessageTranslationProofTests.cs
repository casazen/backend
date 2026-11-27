using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02/T02: the translation of a message, on the real service over a database. The proof the brief asks for: the names, the e-mail addresses and
/// the phone numbers of a text never reach the AI provider, which gets the text scrubbed. Around it, the rules that make the translation safe to offer: the
/// AI first and the channel manager second, <c>translation_unavailable</c> with neither, nothing stored (the message keeps its original), the conversation
/// read in the reach of the caller, and the budget and the cache of the AI.
/// </summary>
public class MessageTranslationProofTests : IAsyncLifetime
{
    private const string Secret = "Ciao Giulia, sono Jonas Berg. Mi trovi a jonas.berg@example.com o al 333 123 4567. Arrivo alle 15.";

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Mock<IAiProvider> _ai = new();
    private readonly Mock<IOctorateApiClient> _client = new();
    // Both on: the connector of the channel manager and the flag of the AI translation (off by default in the application, see the proofs of the flag below).
    private readonly TestFeatureFlags _flags = new(FeatureFlags.OctorateConnector, FeatureFlags.MessageTranslationAi);
    private readonly AiResponseCache _cache = new(Options.Create(new AiCacheOptions()));
    private readonly CapturingLogger<MessageTranslationService> _log = new();
    private readonly List<AiRequest> _requests = [];
    private MessageHubWorld _world = null!;
    private OtaMessage _guestMessage = null!;

    public async Task InitializeAsync()
    {
        _world = await MessageHubTestKit.SeedAsync(_db);

        // The person who hosts is called by a name, and the guest's message carries a name, an address and a number.
        var org = await _db.Orgs.FirstAsync(o => o.Id == _world.Scope.OrgId);
        org.HostName = "Giulia";
        _guestMessage = new OtaMessage
        {
            OrgId = _world.Scope.OrgId,
            ThreadId = _world.GrantedReservation.Id,
            OctorateMessageId = "70123",
            Direction = OtaMessageDirection.Inbound,
            Text = Secret,
            SentAt = MessageHubTestKit.Now.UtcDateTime,
            DeliveryState = OtaMessageDeliveryState.Sent,
            Origin = MessageOrigin.Guest,
            CreatedAt = MessageHubTestKit.Now.UtcDateTime,
        };
        _db.OtaMessages.Add(_guestMessage);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private MessageTranslationService Service() => new(
        _db,
        _ai.Object,
        _cache,
        _client.Object,
        new OctorateOrgScope(NullTenantContext.Instance),
        _flags,
        _log);

    private static MessageThreadKey Ota(OtaMessageThread thread) => MessageThreadKey.ForOta(thread.Id);

    private MessageThreadKey Key => Ota(_world.GrantedReservation);

    /// <summary>The AI answers by repeating the text it was given between the markers, behind «EN: », and the requests it got are kept.</summary>
    private void AiRepeatsWhatItGets(bool providerConfigured = true) =>
        _ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest request, AiModelTier _, string _, CancellationToken _) =>
            {
                _requests.Add(request);
                var between = request.Prompt.Split("<<<", 2)[1].Split(">>>", 2)[0].Trim();
                return Task.FromResult(new AiGenerationResult("EN: " + between, 100, 80, AiModelTier.Economy, false, providerConfigured));
            });

    private void AiFails(Exception failure) =>
        _ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    // ---- The proof of the brief ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: privacy rule (FD-21, AI-06). The names the conversation knows (the guest, the person who hosts), the e-mail addresses and the phone
    /// numbers of a message never reach the AI provider: the prompt and the rules it is given hold none of them, only placeholders. The reader sees the names
    /// back in the translation; the contact data stays in the original.
    /// </summary>
    [Fact]
    public async Task MessageTranslation_PersonalData_ScrubbedBeforeProvider_Test()
    {
        AiRepeatsWhatItGets();

        var translation = await Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en");

        var request = Assert.Single(_requests);
        var everythingTheProviderGot = request.ToTextPrompt();
        foreach (var secret in new[] { "Giulia", "Jonas", "Berg", "jonas.berg@example.com", "example.com", "333 123 4567", "3331234567" })
            Assert.DoesNotContain(secret, everythingTheProviderGot, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("[email]", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[tel]", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[[N1]]", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(AiModelTier.Economy, _ai.Invocations.Select(i => (AiModelTier)i.Arguments[1]).Single());

        // The names are put back for the reader of the translation; the address and the number are not.
        Assert.Equal(MessageTranslationProvider.Ai, translation.Provider);
        Assert.True(translation.MachineTranslated);
        Assert.Equal("en", translation.TargetLanguage);
        Assert.StartsWith("EN: Ciao Giulia, sono Jonas Berg.", translation.Text, StringComparison.Ordinal);
        Assert.Contains("[email]", translation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("jonas.berg@example.com", translation.Text, StringComparison.Ordinal);

        // A text the host is writing is scrubbed the same way.
        _requests.Clear();
        await Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Sono Giulia, chiamami al +39 340 765 4321 o scrivi a giulia@example.org", "en");
        var written = Assert.Single(_requests).ToTextPrompt();
        foreach (var secret in new[] { "Giulia", "340 765 4321", "giulia@example.org" })
            Assert.DoesNotContain(secret, written, StringComparison.OrdinalIgnoreCase);

        // Nothing of the words or the translation is in the log.
        Assert.DoesNotContain("Jonas", _log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jonas.berg", _log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EN:", _log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (FD-21). A name of short words, «Xu Ma», is not left to the provider because its words are short: the person who hosts is called
    /// by it, the draft names it, and the prompt holds a token; the lone «ma» (but) of the draft is still a word.
    /// </summary>
    [Fact]
    public async Task MessageTranslation_AShortNameOfTheConversation_NeverReachesTheProvider_Test()
    {
        AiRepeatsWhatItGets();
        var org = await _db.Orgs.FirstAsync(o => o.Id == _world.Scope.OrgId);
        org.HostName = "Xu Ma";
        await _db.SaveChangesAsync();

        var translation = await Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Ciao, sono Xu Ma e vi aspetto, ma arrivate tardi?", "en");

        var prompt = Assert.Single(_requests).Prompt;
        Assert.DoesNotContain("Xu Ma", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sono [[N1]] e vi aspetto, ma arrivate", prompt, StringComparison.Ordinal);
        Assert.Equal("EN: Ciao, sono Xu Ma e vi aspetto, ma arrivate tardi?", translation.Text);
    }

    // ---- The AI only behind its own flag ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: privacy rule (FD-21, AI-06). With <c>Features:MessageTranslationAi</c> off, which is the default, nothing a guest wrote reaches the AI provider:
    /// not the text of a message, not the draft the host is writing, even when a provider is configured and would answer; the memory of its answers is not
    /// read either (an answer kept for these very words is not served). The channel manager translates the message it holds, by its id; a text only the AI
    /// could translate, and a message the channel manager cannot translate, are 422 <c>translation_unavailable</c>.
    /// </summary>
    [Fact]
    public async Task MessageTranslation_AiFlagOff_NeverCallsTheProvider_Test()
    {
        _flags.Set(FeatureFlags.MessageTranslationAi, false);
        AiRepeatsWhatItGets();
        _client
            .Setup(c => c.TranslateChatMessageAsync("ACC-1", _world.GrantedReservation.OctorateThreadId, "70123", "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello, from the channel manager");

        // The very words were translated before, with the flag on: that answer stays in the memory of the AI and must not be served now.
        _flags.Set(FeatureFlags.MessageTranslationAi, true);
        await Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en");
        Assert.Single(_requests);
        _requests.Clear();
        _ai.Invocations.Clear();
        _flags.Set(FeatureFlags.MessageTranslationAi, false);

        var byMessage = await Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en");
        Assert.Equal(MessageTranslationProvider.ChannelManager, byMessage.Provider);
        Assert.Equal("Hello, from the channel manager", byMessage.Text);

        // The text of a draft has no message on the channel manager: only the AI could translate it, and it is off.
        var draft = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, null, Secret, "en"));
        Assert.Equal("translation_unavailable", draft.Code);

        // A message the channel manager cannot translate is not translated by the AI instead.
        _client
            .Setup(c => c.TranslateChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en"));
        Assert.Equal("translation_unavailable", refused.Code);

        Assert.Empty(_requests);
        _ai.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _ai.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The flag is read at every call, and it is off in the application unless somebody turns it on (a missing value is off).</summary>
    [Fact]
    public void MessageTranslationAi_IsOffUnlessConfigured()
    {
        var empty = new ConfigurationBuilder().Build();
        var on = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:MessageTranslationAi"] = "true" })
            .Build();

        Assert.False(ConfigurationFeatureFlags.IsEnabled(empty, FeatureFlags.MessageTranslationAi));
        Assert.True(ConfigurationFeatureFlags.IsEnabled(on, FeatureFlags.MessageTranslationAi));
        Assert.Contains(FeatureFlags.MessageTranslationAi, FeatureFlags.All);
    }

    // ---- Never stored -----------------------------------------------------------------------------------------------------------------------

    /// <summary>The translation is for the screen: the conversation is exactly as it was after it, the original text included.</summary>
    [Fact]
    public async Task Translate_NeverStoresAnything_TheMessageKeepsItsOriginal()
    {
        AiRepeatsWhatItGets();
        var before = await SnapshotAsync();

        await Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "EN");
        await Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Un testo da tradurre", "fr");

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(Secret, (await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.Id == _guestMessage.Id)).Text);
    }

    private async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
    {
        Messages = await _db.OtaMessages.AsNoTracking().OrderBy(m => m.Id).Select(m => new { m.Id, m.Text, m.OctorateMessageId, m.DeliveryState }).ToListAsync(),
        Threads = await _db.OtaMessageThreads.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.UnreadCount, t.LastMessageAt }).ToListAsync(),
        Templates = await _db.MessageTemplates.AsNoTracking().CountAsync(),
        Activity = await _db.OrgActivityEntries.AsNoTracking().CountAsync(),
    });

    // ---- Who translates ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("notconfigured")]
    [InlineData("budget")]
    [InlineData("failure")]
    [InlineData("nothing")]
    public async Task Translate_WhenTheAiCannotBeUsed_TheChannelManagerTranslatesTheMessage(string why)
    {
        switch (why)
        {
            case "notconfigured":
                AiRepeatsWhatItGets(providerConfigured: false);
                break;
            case "budget":
                AiFails(new AiBudgetExceededException());
                break;
            case "failure":
                AiFails(new HttpRequestException("down"));
                break;
            default:
                _ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiGenerationResult("   ", 10, 0, AiModelTier.Economy, false));
                break;
        }

        _client
            .Setup(c => c.TranslateChatMessageAsync("ACC-1", _world.GrantedReservation.OctorateThreadId, "70123", "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello, I am here from the channel manager");

        var translation = await Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en");

        Assert.Equal(MessageTranslationProvider.ChannelManager, translation.Provider);
        Assert.Equal("Hello, I am here from the channel manager", translation.Text);
        Assert.True(translation.MachineTranslated);
        _client.Verify(c => c.TranslateChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Translate_WithNeitherTranslator_Is422TranslationUnavailable()
    {
        AiRepeatsWhatItGets(providerConfigured: false);
        _client
            .Setup(c => c.TranslateChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en"));

        Assert.Equal("translation_unavailable", error.Code);
        Assert.Equal("MessageTranslationUnavailable", error.MessageKey);
    }

    /// <summary>A text the host is writing has no message on the channel manager, so only the AI can translate it; and a channel manager that is off is not asked.</summary>
    [Fact]
    public async Task Translate_AWrittenText_NeedsTheAi_AndAChannelManagerThatIsOffIsNotAsked()
    {
        AiRepeatsWhatItGets(providerConfigured: false);

        var written = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Ciao a tutti", "en"));
        _flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, null, "en"));

        Assert.Equal("translation_unavailable", written.Code);
        Assert.Equal("translation_unavailable", off.Code);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Translate_TheSameWordsTwice_AsksTheProviderOnce()
    {
        AiRepeatsWhatItGets();

        var first = await Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Ciao a tutti", "en");
        var second = await Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Ciao a tutti", "en");

        Assert.Equal(first.Text, second.Text);
        Assert.Single(_requests);
    }

    // ---- What may be asked --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("klingon")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Translate_ALanguageThatIsNotOneOfThe14_Is422_AndNothingIsAsked(string? language)
    {
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "Ciao", language));

        Assert.Equal("translation_language_invalid", error.Code);
        _ai.VerifyNoOtherCalls();
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Translate_AMessageAndATextTogether_OrNeither_Is422()
    {
        var both = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, _guestMessage.Id, "Ciao", "en"));
        var neither = await Assert.ThrowsAsync<DomainRuleException>(() => Service().TranslateAsync(_world.Scope.OrgWide, Key, null, "   ", "en"));

        Assert.Equal("translation_input_invalid", both.Code);
        Assert.Equal("translation_input_invalid", neither.Code);
        _ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Translate_ATextLongerThanAMessage_Is422()
    {
        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().TranslateAsync(_world.Scope.OrgWide, Key, null, new string('a', GuestMessageRules.TextMaxLength + 1), "en"));

        Assert.Equal("message_text_invalid", error.Code);
        _ai.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A collaborator limited to some houses translates the messages of the conversations of its houses; a conversation of another house, one with no house,
    /// another org's and a message of a conversation other than the one named are a 404, and no provider is asked.
    /// </summary>
    [Fact]
    public async Task Translate_Collaborator_ReachesOnlyTheConversationsOfItsHouses_AndTheMessagesOfTheConversationNamed()
    {
        AiRepeatsWhatItGets();
        var collaborator = _world.Scope.Restricted;

        foreach (var thread in new[] { _world.HiddenReservation, _world.Unassigned, _world.OtherOrg })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => Service().TranslateAsync(collaborator, Ota(thread), null, "Ciao", "en"));
            Assert.Equal("thread_not_found", error.Code);
        }

        // A message of another conversation, even one the collaborator reaches, is not found through this one.
        var request = await _db.OtaMessages.AsNoTracking().FirstAsync(m => m.ThreadId == _world.GrantedRequest.Id);
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => Service().TranslateAsync(collaborator, Key, request.Id, null, "en"));
        Assert.Equal("message_not_found", foreign.Code);
        var hiddenMessage = await _db.OtaMessages.AsNoTracking().FirstAsync(m => m.ThreadId == _world.HiddenReservation.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().TranslateAsync(collaborator, Key, hiddenMessage.Id, null, "en"));

        Assert.Empty(_requests);
        Assert.NotNull(await Service().TranslateAsync(collaborator, Key, _guestMessage.Id, null, "en"));
    }

    [Fact]
    public async Task Translate_AMessageOfAConversationWithTheGuestOfADirectBooking_IsReadFromTheDirectMessages()
    {
        AiRepeatsWhatItGets();
        var thread = await _db.GuestThreads.AsNoTracking().FirstAsync(t => t.BookingId == _world.GrantedDirectBooking.Id);
        var message = await _db.GuestMessages.AsNoTracking().FirstAsync(m => m.ThreadId == thread.Id);

        var translation = await Service().TranslateAsync(_world.Scope.OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), message.Id, null, "en");

        Assert.Equal(MessageTranslationProvider.Ai, translation.Provider);
        Assert.StartsWith("EN: Direct question", translation.Text, StringComparison.Ordinal);
    }
}
