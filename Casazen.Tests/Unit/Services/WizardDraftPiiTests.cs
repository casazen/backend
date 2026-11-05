using System.Text.Json;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-024-R2 (privacy): the fields that name a person never stay in the plain half of a wizard draft, at any depth of what the person
/// typed, and the two halves put together give back exactly what was typed (NF-03: never lose data).
/// </summary>
public class WizardDraftPiiTests
{
    // A booking with the booker's data at the top, a guest list with personal fields in some elements, an object that is personal as a
    // whole (address), a null personal field and a field spelled with another case (GuestEmail).
    private const string Typed = """
        {
          "step": "ospiti",
          "propertyName": "Villa del Lago (demo)",
          "notes": ["pulizia finale", "arrivo tardi"],
          "guestName": "Mario Rossi",
          "GuestEmail": "mario.rossi@example.com",
          "address": { "street": "Via Segretissima 7", "city": "Como" },
          "price": { "nightly": 180.50, "currency": "EUR" },
          "guests": [
            { "room": 1, "adult": true, "guestName": "Giulia Verdi", "documentNumber": "CA12345AB" },
            { "room": 2, "adult": false },
            { "room": 3, "birthDate": null, "emergencyContact": { "name": "Anna Bianchi", "phone": "+393331234567" } }
          ]
        }
        """;

    private static readonly string[] Secrets =
        ["Mario Rossi", "mario.rossi@example.com", "Via Segretissima", "Giulia Verdi", "CA12345AB", "Anna Bianchi", "+393331234567"];

    [Fact]
    public void Split_PersonalFieldsAtAnyDepth_NeverStayInThePlainHalf()
    {
        using var typed = JsonDocument.Parse(Typed);

        var split = WizardDraftPii.Split(typed.RootElement);

        // Neither the values nor the names of the personal fields (whatever their case) are in the plain half.
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, split.PlainJson, StringComparison.OrdinalIgnoreCase);
        foreach (var name in WizardDraftPiiFields.PII_FIELD_NAMES)
            Assert.DoesNotContain($"\"{name}\"", split.PlainJson, StringComparison.OrdinalIgnoreCase);

        // What is not personal stays, in the same shape: the list keeps its three elements.
        using var plain = JsonDocument.Parse(split.PlainJson);
        var guests = plain.RootElement.GetProperty("guests");
        Assert.Equal(3, guests.GetArrayLength());
        Assert.Equal(1, guests[0].GetProperty("room").GetInt32());
        Assert.Equal(3, guests[2].GetProperty("room").GetInt32());
        Assert.Equal("Villa del Lago (demo)", plain.RootElement.GetProperty("propertyName").GetString());
        Assert.Equal(180.50m, plain.RootElement.GetProperty("price").GetProperty("nightly").GetDecimal());

        // The personal half holds them all, under the path that leads to them (a null placeholder for the guest with none).
        Assert.NotNull(split.PersonalJson);
        using var personal = JsonDocument.Parse(split.PersonalJson!);
        Assert.Equal("Mario Rossi", personal.RootElement.GetProperty("guestName").GetString());
        Assert.Equal("mario.rossi@example.com", personal.RootElement.GetProperty("GuestEmail").GetString());
        Assert.Equal("Via Segretissima 7", personal.RootElement.GetProperty("address").GetProperty("street").GetString());
        var personalGuests = personal.RootElement.GetProperty("guests");
        Assert.Equal("CA12345AB", personalGuests[0].GetProperty("documentNumber").GetString());
        Assert.Equal(JsonValueKind.Null, personalGuests[1].ValueKind);
        Assert.Equal("+393331234567", personalGuests[2].GetProperty("emergencyContact").GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, personalGuests[2].GetProperty("birthDate").ValueKind);
    }

    [Fact]
    public void Merge_ThePlainAndThePersonalHalves_GiveBackExactlyWhatWasTyped()
    {
        using var typed = JsonDocument.Parse(Typed);
        var split = WizardDraftPii.Split(typed.RootElement);

        using var merged = WizardDraftPii.Merge(split.PlainJson, split.PersonalJson);

        Assert.Equal(Canonical(typed.RootElement), Canonical(merged.RootElement));
    }

    [Fact]
    public void Split_DataWithNoPersonalField_HasNoPersonalHalf_AndMergeReturnsItUnchanged()
    {
        using var typed = JsonDocument.Parse("""{"step":"prezzi","nightly":120,"tags":["mare","lago"],"rooms":[{"beds":2},{"beds":1}]}""");

        var split = WizardDraftPii.Split(typed.RootElement);

        Assert.Null(split.PersonalJson);
        using var merged = WizardDraftPii.Merge(split.PlainJson, split.PersonalJson);
        Assert.Equal(Canonical(typed.RootElement), Canonical(merged.RootElement));
    }

    [Fact]
    public void Split_DataThatIsNotAnObject_IsRefused()
    {
        using var list = JsonDocument.Parse("""["guestName"]""");

        Assert.Throws<ArgumentException>(() => WizardDraftPii.Split(list.RootElement));
    }

    /// <summary>The same JSON whatever the order of the properties: objects sorted by name, strings and numbers as they are.</summary>
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(
            ",",
            element.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{JsonSerializer.Serialize(p.Name)}:{Canonical(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        _ => element.GetRawText(),
    };
}
