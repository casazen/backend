using System.Reflection;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T01 (NF-66, NF-67, PO-09): the tables of the assistant. The privacy rule has its proof (the free text of a host is a Data Protection payload at rest);
/// the others are the shape the brief asks for: the unique indexes that keep one rules row per organization and one decision per message, the defaults (threshold
/// 0.85, a new situation in draft), the provenance of the direct messages, and a log with no text.
/// </summary>
public class AssistantEntitiesProofTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>
    /// Privacy rule: the text of a knowledge entry, the text a lesson proposes and the response template of a situation (a Wi-Fi password or a note of the host can be in any of
    /// them) are stored encrypted, with a purpose of their own, and have no column length (the payload is longer than the text). What the column holds is a Data Protection
    /// payload with none of the words; only the converter of that purpose reads it back, and not the one of the conversations' words. Written, not run locally against PostgreSQL:
    /// the EF InMemory store keeps the values the model hands it, so the proof is on the model and its converters.
    /// </summary>
    [Fact]
    public void AssistantKnowledge_TextIsEncryptedAtRest_Test()
    {
        const string wifi = "Rete CasaZenOstuni, password Zefirino-2026-LunaPiena";
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var conversationWords = Provider.CreateProtector(EncryptedColumns.OtaMessageTextPurpose);

        foreach (var (entityType, property, longest) in new[]
                 {
                     (typeof(PropertyKnowledgeEntry), nameof(PropertyKnowledgeEntry.Text), PropertyKnowledgeEntry.TextMaxLength),
                     (typeof(AssistantLesson), nameof(AssistantLesson.ProposedText), AssistantLesson.ProposedTextMaxLength),
                     (typeof(AssistantSituationSetting), nameof(AssistantSituationSetting.ResponseTemplate), AssistantSituationSetting.TemplateMaxLength),
                 })
        {
            Assert.Contains(
                EncryptedColumns.All,
                c => c.EntityType == entityType && c.Property == property && c.Purpose == EncryptedColumns.AssistantContentPurpose);

            var column = db.Model.FindEntityType(entityType)!.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());

            // No length on the column: the payload of a text of the longest size does not fit in a column of that size.
            Assert.Null(column.GetMaxLength());
            var longestText = new string('a', longest);
            Assert.True(((string)converter.ConvertToProvider(longestText)!).Length > longest);

            var stored = (string)converter.ConvertToProvider(wifi)!;

            // What the column holds: a payload, and nothing of the words in it.
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(wifi, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("Zefirino", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("password", stored, StringComparison.OrdinalIgnoreCase);

            // Read back only through the decryption of the assistant's purpose: the protector of the words of the conversations cannot read it.
            Assert.Equal(wifi, converter.ConvertFromProvider(stored));
            Assert.ThrowsAny<CryptographicException>(() => conversationWords.Unprotect(stored));
        }

        Assert.Equal("Casazen.Assistant.Content", EncryptedColumns.AssistantContentPurpose);
        Assert.True(EncryptedColumns.IsConfigured(db));
    }

    [Fact]
    public void AssistantDecisionLog_HoldsIdsAndCodesAndNoText()
    {
        // PO-09 minimization: the log names the message by its id, the conversation by its key, the sources by ids and topics. No string member can carry a word of the guest.
        var strings = typeof(AssistantDecisionLog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(AssistantDecisionLog.ApproverUserId),
                nameof(AssistantDecisionLog.Model),
                nameof(AssistantDecisionLog.ModelVersion),
                nameof(AssistantDecisionLog.SourceRefs),
                nameof(AssistantDecisionLog.ThreadKey),
            },
            strings);
        Assert.DoesNotContain(EncryptedColumns.All, c => c.EntityType == typeof(AssistantDecisionLog));
    }

    [Fact]
    public void Model_OneRulesRowPerOrganization_OneExceptionsRowPerProperty_OneDecisionPerMessage()
    {
        using var db = NewNpgsqlContext();

        var rules = db.Model.FindEntityType(typeof(OrgAssistantRules))!;
        var rulesIndex = Assert.Single(rules.GetIndexes(), i => i.GetDatabaseName() == OrgAssistantRules.OrgIndexName);
        Assert.True(rulesIndex.IsUnique);
        Assert.Equal(new[] { nameof(OrgAssistantRules.OrgId) }, rulesIndex.Properties.Select(p => p.Name));
        Assert.Equal("OrgAssistantRules", rules.GetTableName());

        var settings = db.Model.FindEntityType(typeof(PropertyAssistantSettings))!;
        var settingsIndex = Assert.Single(settings.GetIndexes(), i => i.GetDatabaseName() == PropertyAssistantSettings.OrgPropertyIndexName);
        Assert.True(settingsIndex.IsUnique);
        Assert.Equal(
            new[] { nameof(PropertyAssistantSettings.OrgId), nameof(PropertyAssistantSettings.PropertyId) },
            settingsIndex.Properties.Select(p => p.Name));

        var log = db.Model.FindEntityType(typeof(AssistantDecisionLog))!;
        var logIndex = Assert.Single(log.GetIndexes(), i => i.GetDatabaseName() == AssistantDecisionLog.InboundMessageIndexName);
        Assert.True(logIndex.IsUnique);
        Assert.Equal(new[] { nameof(AssistantDecisionLog.InboundMessageId) }, logIndex.Properties.Select(p => p.Name));
        Assert.Contains(
            log.GetIndexes(),
            i => !i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(AssistantDecisionLog.OrgId), nameof(AssistantDecisionLog.PropertyId), nameof(AssistantDecisionLog.CreatedAt)]));

        // The version of the instructions is the concurrency token of the rules: two changes at the same moment cannot claim the same version.
        Assert.True(rules.FindProperty(nameof(OrgAssistantRules.InstructionVersion))!.IsConcurrencyToken);
    }

    [Fact]
    public void Model_TheSituationSettings_HaveTwoPartialUniqueIndexes_OneForTheOrganizationAndOneForTheProperties()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(AssistantSituationSetting))!;

        var organization = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == AssistantSituationSetting.OrgSituationIndexName);
        Assert.True(organization.IsUnique);
        Assert.Equal(new[] { nameof(AssistantSituationSetting.OrgId), nameof(AssistantSituationSetting.Situation) }, organization.Properties.Select(p => p.Name));
        Assert.Equal("\"PropertyId\" IS NULL", organization.GetFilter());

        var property = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == AssistantSituationSetting.PropertySituationIndexName);
        Assert.True(property.IsUnique);
        Assert.Equal(
            new[] { nameof(AssistantSituationSetting.OrgId), nameof(AssistantSituationSetting.PropertyId), nameof(AssistantSituationSetting.Situation) },
            property.Properties.Select(p => p.Name));
        Assert.Equal("\"PropertyId\" IS NOT NULL", property.GetFilter());
    }

    [Fact]
    public void Model_TheIndexesOfTheKnowledgeBaseTheLessonsAndTheTrustProposals_Exist()
    {
        using var db = NewNpgsqlContext();

        Assert.Contains(
            db.Model.FindEntityType(typeof(PropertyKnowledgeEntry))!.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(PropertyKnowledgeEntry.OrgId), nameof(PropertyKnowledgeEntry.PropertyId), nameof(PropertyKnowledgeEntry.Topic)]));
        Assert.Contains(
            db.Model.FindEntityType(typeof(AssistantLesson))!.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(AssistantLesson.OrgId), nameof(AssistantLesson.Status)]));
        Assert.Contains(
            db.Model.FindEntityType(typeof(AssistantTrustProposal))!.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(AssistantTrustProposal.OrgId), nameof(AssistantTrustProposal.Status)]));
    }

    [Fact]
    public void Defaults_TheThresholdIs085_TheModeOfANewSituationIsDraft_TheAssistantIsOffAndTheVersionStartsAtOne()
    {
        var rules = new OrgAssistantRules();

        Assert.False(rules.Enabled);
        Assert.Equal(AssistantTone.Neutral, rules.Tone);
        Assert.Equal(new[] { "it", "en" }, rules.Languages);
        Assert.Equal(0.85m, rules.ConfidenceThreshold);
        Assert.Equal(OrgAssistantRules.DefaultConfidenceThreshold, rules.ConfidenceThreshold);
        Assert.Equal(1, rules.InstructionVersion);
        Assert.Empty(rules.AllowedTopics);
        Assert.Empty(rules.ForbiddenTopics);
        Assert.Null(rules.HoursFrom);
        Assert.Null(rules.HoursTo);

        // Each organization starts from its own list: the default is a copy, never the shared one.
        rules.Languages.Add("de");
        Assert.Equal(new[] { "it", "en" }, new OrgAssistantRules().Languages);

        Assert.Equal(AssistantMode.Draft, new AssistantSituationSetting().Mode);

        // A null field of the exceptions of a property means «as the organization».
        var exceptions = new PropertyAssistantSettings();
        Assert.Null(exceptions.Enabled);
        Assert.Null(exceptions.Tone);
        Assert.Null(exceptions.Signature);
        Assert.Null(exceptions.HoursFrom);
        Assert.Null(exceptions.HoursTo);
        Assert.Null(exceptions.ConfidenceThreshold);
        Assert.Null(exceptions.ForbiddenTopics);

        Assert.Equal(LessonStatus.Proposed, new AssistantLesson().Status);
        Assert.Null(new AssistantLesson().Scope);
        Assert.Equal(TrustProposalStatus.Proposed, new AssistantTrustProposal().Status);
        Assert.True(new PropertyKnowledgeEntry().IsActive);
        Assert.Equal(KnowledgeSource.Custom, new PropertyKnowledgeEntry().Source);
    }

    [Fact]
    public void GuestMessage_SaysWhoWroteItAndByWhichRule_AndAnUnsetOriginReadsAsTheGuests()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(GuestMessage))!;

        var origin = entity.FindProperty(nameof(GuestMessage.Origin))!;
        Assert.Equal(typeof(MessageOrigin), origin.ClrType);
        Assert.False(origin.IsNullable);
        Assert.Equal((int)MessageOrigin.Guest, Convert.ToInt32(origin.GetDefaultValue(), System.Globalization.CultureInfo.InvariantCulture));

        var ruleCode = entity.FindProperty(nameof(GuestMessage.RuleCode))!;
        Assert.True(ruleCode.IsNullable);
        Assert.Equal(64, ruleCode.GetMaxLength());
        Assert.Equal(OtaMessage.RuleCodeMaxLength, GuestMessage.RuleCodeMaxLength);
    }

    [Fact]
    public void GuestMessageRules_OriginOf_AMessageOfTheGuestIsTheGuests_AMessageOfTheHostIsAPersons_AndTheMigrationSaysTheSame()
    {
        Assert.Equal(MessageOrigin.Guest, GuestMessageRules.OriginOf(GuestMessageSender.Guest));
        Assert.Equal(MessageOrigin.Person, GuestMessageRules.OriginOf(GuestMessageSender.Host));

        // The migration gave the messages that existed the origin the writers give a new one: the statement is written with the numbers of the two enums, and this fails if they move.
        Assert.Equal(1, (int)GuestMessageSender.Host);
        Assert.Equal(2, (int)MessageOrigin.Person);
        var migration = Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "Migrations"), "*_AddMessageAssistant.cs")
            .Select(File.ReadAllText)
            .Single();
        Assert.Contains("UPDATE \\\"GuestMessages\\\" SET \\\"Origin\\\" = 2 WHERE \\\"Sender\\\" = 1;", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValuesOfTheEnums_AreStoredAsNumbers_AndNeverMove()
    {
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 99 },
            Enum.GetValues<AssistantSituation>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<AssistantMode>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<AssistantUrgency>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<AssistantDecision>().Select(v => (int)v).Order());
        Assert.Equal(
            new[] { 0, 1, 2, 4, 8, 16, 32, 64 },
            Enum.GetValues<SensitiveCategory>().Select(v => (int)v).Order());
        Assert.Equal(Enumerable.Range(1, 11), Enum.GetValues<KnowledgeTopic>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<KnowledgeEntryKind>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2 }, Enum.GetValues<KnowledgeSource>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<AssistantTone>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2 }, Enum.GetValues<LessonKind>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<LessonStatus>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<LessonScope>().Select(v => (int)v).Order());
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<TrustProposalStatus>().Select(v => (int)v).Order());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);
}
