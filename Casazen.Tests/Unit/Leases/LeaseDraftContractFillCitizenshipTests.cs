using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Leases.Reading;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// CF-DONE: the contract of the guided service states the citizenship of the tenant and not the landlord's, and the review of an imported draft
/// blocks without it. For the draft of the service a landlord who is a person born in Italy is taken as Italian and the field says it is
/// derived; nobody else is guessed (not the tenant, whose citizenship decides the communication to the Questura, not a landlord born abroad),
/// and what was typed is never overwritten.
/// </summary>
public class LeaseDraftContractFillCitizenshipTests
{
    private const string LandlordCode = "RSSMRA80A01H501U"; // born in Roma
    private const string TenantCode = "VRDGLI85B42F205E"; // born in Milano

    [Fact]
    public void ALandlordBornInItaly_IsTakenAsItalian_AndTheFieldSaysItIsDerived_ForTheServicesDraft()
    {
        var fill = LeaseDraftContractFill.Merge(
            new LeaseDraftValues(), Reading(LandlordCode), LeaseOrigin.Imported, Guid.NewGuid, deriveLandlordCitizenship: true);

        Assert.Equal("IT", Assert.Single(fill.Values.Landlords).Citizenship);
        var derived = Assert.Single(fill.Fields, f => f is { Field: "citizenship", Derived: true });
        Assert.Equal(PartyRole.Landlord, derived.Role);
        Assert.Equal(FieldReadingState.Read, derived.State);
        // The tenant's citizenship is never guessed: the contract did not state it, so it stays to be asked.
        Assert.True(string.IsNullOrEmpty(Assert.Single(fill.Values.Tenants).Citizenship));
        Assert.DoesNotContain(fill.Fields, f => f.Field == "citizenship" && f.Role == PartyRole.Tenant);
    }

    [Fact]
    public void WithoutTheServicesDraft_NothingIsDerived()
    {
        var fill = LeaseDraftContractFill.Merge(new LeaseDraftValues(), Reading(LandlordCode), LeaseOrigin.Imported, Guid.NewGuid);

        Assert.True(string.IsNullOrEmpty(Assert.Single(fill.Values.Landlords).Citizenship));
        Assert.DoesNotContain(fill.Fields, f => f.Field == "citizenship");
    }

    [Fact]
    public void ALandlordBornAbroad_IsNotGuessed()
    {
        var foreign = WithCheckCharacter("RSSMRA80A01Z404"); // born in the United States

        Assert.True(ItalianFiscalCode.IsValid(foreign));
        Assert.False(ItalianFiscalCode.IsPersonBornInItaly(foreign));
        var fill = LeaseDraftContractFill.Merge(
            new LeaseDraftValues(), Reading(foreign), LeaseOrigin.Imported, Guid.NewGuid, deriveLandlordCitizenship: true);

        Assert.True(string.IsNullOrEmpty(Assert.Single(fill.Values.Landlords).Citizenship));
    }

    [Fact]
    public void WhatWasTyped_IsNeverOverwrittenByTheDerivation()
    {
        var typed = new LeaseDraftValues
        {
            Landlords = [new LeaseDraftParty { Id = Guid.NewGuid(), FiscalCode = LandlordCode, Citizenship = "FR" }],
        };

        var fill = LeaseDraftContractFill.Merge(typed, Reading(LandlordCode), LeaseOrigin.Imported, Guid.NewGuid, deriveLandlordCitizenship: true);

        Assert.Equal("FR", Assert.Single(fill.Values.Landlords).Citizenship);
        Assert.DoesNotContain(fill.Fields, f => f is { Field: "citizenship", Derived: true });
    }

    [Theory]
    [InlineData(LandlordCode, true)]
    [InlineData(TenantCode, true)]
    [InlineData("rssmra80a01h501u", true)]
    [InlineData("RSSMRA80A01H501X", false)] // wrong check character
    [InlineData("12345", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPersonBornInItaly_OnlyForTheValidCodeOfAPersonWhosePlaceOfBirthIsAMunicipality(string? code, bool expected) =>
        Assert.Equal(expected, ItalianFiscalCode.IsPersonBornInItaly(code));

    /// <summary>A contract that names a landlord and a tenant by their fiscal codes and residence, and states no citizenship.</summary>
    private static LeaseContractReading Reading(string landlordCode) =>
        new()
        {
            Parties =
            [
                Party(PartyRole.Landlord, landlordCode, "Mario", "Rossi", "Roma (RM), Via dei Mille n. 5"),
                Party(PartyRole.Tenant, TenantCode, "Giulia", "Verdi", "Milano (MI), Via Argiro n. 7"),
            ],
        };

    private static ReadParty Party(PartyRole role, string code, string first, string last, string residence) =>
        new()
        {
            Role = role,
            RoleConfidence = 95,
            FiscalCode = code,
            FiscalCodeConfidence = 95,
            FirstName = first,
            LastName = last,
            NameConfidence = 95,
            Residence = residence,
            ResidenceConfidence = 90,
        };

    /// <summary>The 15 characters of a code with the check character that makes it valid.</summary>
    private static string WithCheckCharacter(string first15) => first15 + ItalianFiscalCode.CheckCharacter(first15.AsSpan());
}
