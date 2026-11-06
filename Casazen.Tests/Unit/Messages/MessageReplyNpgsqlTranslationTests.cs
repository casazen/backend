using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02: the queries of the reply, of the choice of the channel, of the templates and of the lookup of a conversation, run on the PostgreSQL provider
/// with every kind of scope and without a server (see <see cref="NpgsqlTranslationProbe"/>). The in-memory provider accepts what Npgsql cannot turn into SQL;
/// this fails here, before CI and before a host meets it, when the scope, a projection of the guest or a navigation has no translation. The probe reaches
/// the first query of each call (the empty answers end the call there); the lookup of a conversation is given the row of the org so that it goes on to the
/// query of the conversation itself.
/// </summary>
public class MessageReplyNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid TemplateId = Guid.Parse("0f1e2d3c-4b5a-4697-8a9b-0c1d2e3f4a5b");
    private static readonly Guid BookingId = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");
    private static readonly MessageThreadKey OtaKey = MessageThreadKey.ForOta(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly MessageThreadKey DirectKey = MessageThreadKey.ForDirect(BookingId);

    public static TheoryData<string> EveryKindOfScope() => ["collaborator", "collaborator-one-area", "account-in-no-team", "whole-org"];

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "collaborator-one-area" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore", GrantedMode: RentalMode.Short),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    /// <summary>The row of the org the lookup of a conversation reads first (the name of the person who hosts and the name of the organization).</summary>
    private static DataTable? OrgRow(string sql)
    {
        if (!sql.Contains("FROM \"Orgs\"", StringComparison.Ordinal) || !sql.Contains("\"HostName\"", StringComparison.Ordinal))
            return null;

        var table = new DataTable();
        table.Columns.Add("HostName", typeof(string));
        table.Columns.Add("DisplayName", typeof(string));
        table.Rows.Add("Marco", "Casa Demo");
        return table;
    }

    [Theory]
    [MemberData(nameof(EveryKindOfScope))]
    public async Task TheReplyAndTheChoiceOfTheChannel_AreTranslatedToSqlByNpgsql(string kind)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var service = new MessageReplyRig(db).Service;
        var scope = ScopeOf(kind);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.ReplyAsync(scope, OtaKey, "Ciao", "key-12345678", "auth0|persona", MessageOrigin.Person, null));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.ReplyAsync(scope, DirectKey, "Ciao", "key-12345678", "auth0|persona", MessageOrigin.Person, null));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ChooseChannelAsync(scope, BookingId));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.StartConversationAsync(scope, BookingId, "Ciao", "key-12345678", "auth0|persona"));

        // The conversation of the channel, and the booking three times (the direct reply, the choice, the first message).
        Assert.Contains(statements, sql => sql.Contains("FROM \"OtaMessageThreads\"", StringComparison.Ordinal));
        Assert.True(statements.Count(sql => sql.Contains("FROM \"Bookings\"", StringComparison.Ordinal)) >= 3, $"{kind}: the queries of the bookings were not all reached.");

        // Every statement names the org of the scope.
        Assert.All(statements, sql => Assert.Contains("\"OrgId\"", sql, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryKindOfScope))]
    public async Task TheTemplatesAndTheLookupOfAConversation_AreTranslatedToSqlByNpgsql(string kind)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: OrgRow);
        var templates = new MessageTemplateService(
            db,
            Mock.Of<IOctorateApiClient>(),
            new OctorateOrgScope(NullTenantContext.Instance),
            new TestFeatureFlags(FeatureFlags.OctorateConnector),
            NullLogger<MessageTemplateService>.Instance,
            new FixedTimeProvider(MessageHubTestKit.Now));
        var scope = ScopeOf(kind);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.ListAsync(scope, activeOnly: false));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.ListAsync(scope, activeOnly: true));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.CreateAsync(scope, "Benvenuto", "it", "Ciao {{guest_name}}", true));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.UpdateAsync(scope, TemplateId, "Benvenuto", "it", "Ciao", true));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.DeleteAsync(scope, TemplateId));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => templates.RenderAsync(scope, TemplateId, OtaKey));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => MessageConversationLookup.FindAsync(db, scope, OtaKey, CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => MessageConversationLookup.FindAsync(db, scope, DirectKey, CancellationToken.None));

        Assert.Contains(statements, sql => sql.Contains("FROM \"MessageTemplates\"", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("FROM \"OtaMessageThreads\"", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("FROM \"Bookings\"", StringComparison.Ordinal));

        // The templates are the data of the org: every statement that reads them names it.
        Assert.All(
            statements.Where(sql => sql.Contains("FROM \"MessageTemplates\"", StringComparison.Ordinal)),
            sql => Assert.Contains("\"OrgId\"", sql, StringComparison.Ordinal));
    }
}
