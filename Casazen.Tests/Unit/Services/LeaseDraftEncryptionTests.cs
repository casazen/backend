using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12 (the personal data of the parties, decision D29): the values a landlord typed in the wizard (names, fiscal codes, residences)
/// are stored encrypted — the draft's JSON and the residence of a party. The EF InMemory database keeps the values the model hands it
/// and not the stored text, so what the table holds (a payload with none of them) is checked on PostgreSQL in
/// <c>LeaseDraftPostgresTests</c>; here the model, the converters and the round trip through the application's own context.
/// </summary>
public class LeaseDraftEncryptionTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void TheDraftAndTheResidence_HaveTheirOwnPurposes_AndTheirOwnConverters()
    {
        using var h = new LeaseDraftHarness(Provider);

        var draft = h.Db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!;
        var residence = h.Db.Model.FindEntityType(typeof(Party))!.FindProperty(nameof(Party.Residence))!;

        Assert.IsType<EncryptedStringConverter>(draft.GetValueConverter());
        Assert.IsType<EncryptedStringConverter>(residence.GetValueConverter());
        Assert.Equal("Casazen.LeaseContract.Draft", EncryptedColumns.LeaseDraftPurpose);
        Assert.Equal("Casazen.LeaseParty", EncryptedColumns.LeasePartyPurpose);
        Assert.NotEqual(EncryptedColumns.LeaseDraftPurpose, EncryptedColumns.LeasePartyPurpose);
        Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(LeaseContract) && c.Property == nameof(LeaseContract.DraftData));
        Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(Party) && c.Property == nameof(Party.Residence));
        Assert.True(EncryptedColumns.IsConfigured(h.Db));
    }

    [Fact]
    public void WithoutAProvider_TheModelHasNoConverter_SoNothingIsStoredInClearByMistakeInProduction()
    {
        using var h = new LeaseDraftHarness();

        Assert.False(EncryptedColumns.IsConfigured(h.Db));
        Assert.Null(h.Db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!.GetValueConverter());
        Assert.Null(h.Db.Model.FindEntityType(typeof(Party))!.FindProperty(nameof(Party.Residence))!.GetValueConverter());
    }

    [Fact]
    public void TheColumnsHaveNoLengthOfTheirOwn_ThePayloadIsLongerThanTheText()
    {
        using var h = new LeaseDraftHarness(Provider);

        var residence = h.Db.Model.FindEntityType(typeof(Party))!.FindProperty(nameof(Party.Residence))!;
        var draft = h.Db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!;

        Assert.Null(residence.GetMaxLength());
        Assert.Null(draft.GetMaxLength());
        var longest = new string('x', Party.ResidenceMaxLength);
        var payload = Provider.CreateProtector(EncryptedColumns.LeasePartyPurpose).Protect(longest);
        Assert.True(payload.Length > Party.ResidenceMaxLength);
    }

    [Fact]
    public void WhatTheTableHolds_IsAPayloadWithNoneOfTheValues_AndComesBackThroughTheConverter()
    {
        using var h = new LeaseDraftHarness(Provider);
        var typed = new LeaseDraftValues
        {
            PropertyId = Guid.NewGuid(),
            Tenants = [new LeaseDraftParty { Id = Guid.NewGuid(), FirstName = "Marco", LastName = "Bianchi", FiscalCode = "WRONG", Residence = "Via Nascosta 3, Lecce" }],
        }.ToJson();

        var draft = Assert.IsType<EncryptedStringConverter>(h.Db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!.GetValueConverter());
        var residence = Assert.IsType<EncryptedStringConverter>(h.Db.Model.FindEntityType(typeof(Party))!.FindProperty(nameof(Party.Residence))!.GetValueConverter());

        var storedDraft = (string)draft.ConvertToProvider(typed)!;
        var storedResidence = (string)residence.ConvertToProvider("Via Segretissima 7, Bari")!;

        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, storedDraft, StringComparison.Ordinal);
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, storedResidence, StringComparison.Ordinal);
        // The payload is random text, so a short word turns up in it by chance ("Via" about once in 200 runs): only values too long to
        // be met by chance are searched for, with the whole typed values.
        foreach (var secret in new[] { "Bianchi", "Nascosta", "Via Nascosta 3, Lecce", typed })
            Assert.DoesNotContain(secret, storedDraft, StringComparison.OrdinalIgnoreCase);
        foreach (var secret in new[] { "Segretissima", "Via Segretissima 7, Bari" })
            Assert.DoesNotContain(secret, storedResidence, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(typed, draft.ConvertFromProvider(storedDraft));
        Assert.Equal("Via Segretissima 7, Bari", residence.ConvertFromProvider(storedResidence));
    }

    [Fact]
    public void ThePurposeOfOneColumn_DoesNotOpenTheOther_NorTheGuestDocuments()
    {
        using var h = new LeaseDraftHarness(Provider);
        var draft = Assert.IsType<EncryptedStringConverter>(h.Db.Model.FindEntityType(typeof(LeaseContract))!.FindProperty(nameof(LeaseContract.DraftData))!.GetValueConverter());
        var stored = (string)draft.ConvertToProvider("""{"propertyId":"x"}""")!;

        Assert.Equal("""{"propertyId":"x"}""", Provider.CreateProtector(EncryptedColumns.LeaseDraftPurpose).Unprotect(stored));
        Assert.ThrowsAny<CryptographicException>(() => Provider.CreateProtector(EncryptedColumns.LeasePartyPurpose).Unprotect(stored));
        Assert.ThrowsAny<CryptographicException>(() => Provider.CreateProtector(EncryptedColumns.GuestDocumentPurpose).Unprotect(stored));
    }

    [Fact]
    public async Task TheWholeDraft_WorksWithTheEncryptionConfigured_TheValuesComeBackAsTyped()
    {
        using var h = await new LeaseDraftHarness(Provider).SeedAsync();
        var (created, version) = await h.CompleteAsync();
        var saved = await h.SaveAsync(created.Id, version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Via Segretissima 7, Bari"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Via Riservata 9, Bari"},
                        {"firstName":"Marco","lastName":"Bianchi","fiscalCode":"WRONG","residence":"Via Nascosta 3, Lecce"}]}
            """);
        Assert.True(EncryptedColumns.IsConfigured(h.Db));

        var view = await h.NewService().GetAsync(created.Id);

        Assert.Equal("Via Segretissima 7, Bari", view.Values.Landlords.Single().Residence);
        Assert.Equal(["Giulia", "Marco"], view.Values.Tenants.Select(t => t.FirstName));
        Assert.Equal("WRONG", view.Values.Tenants[1].FiscalCode);
        await using var read = h.SecondContext();
        var rows = await read.Parties.AsNoTracking().Where(p => p.LeaseContractId == created.Id).ToListAsync();
        Assert.Contains(rows, p => p.Residence == "Via Segretissima 7, Bari");
        Assert.Contains(rows, p => p.Residence == "Via Riservata 9, Bari");
        Assert.Equal(saved.Version, view.Version);
    }

    [Fact]
    public async Task ADraftThatIsComplete_KeepsNoJson_ThePartiesAreTheOnlyCopyOfTheirData()
    {
        using var h = await new LeaseDraftHarness(Provider).SeedAsync();
        var (created, _) = await h.CompleteAsync();

        await using var plain = h.PlainContext();
        var lease = await plain.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == created.Id);

        Assert.Null(lease.DraftData);
    }
}
