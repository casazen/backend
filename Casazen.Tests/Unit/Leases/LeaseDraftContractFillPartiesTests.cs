using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Leases.Reading;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// The people a contract names go into the draft: a contract with two co-tenants gives a draft with two tenants, also when one of them is
/// already there from an earlier reading. A role in which somebody else was typed keeps what was typed, and the person is only told about the others.
/// </summary>
public class LeaseDraftContractFillPartiesTests
{
    private const string LandlordCode = "RSSMRA80A01H501U";
    private const string FirstTenantCode = "VRDGLI85B42F205E";
    private const string SecondTenantCode = "BNCPLA68L10F704T";
    private const string TypedTenantCode = "DLCNMR71S61A662D";

    [Fact]
    public void TwoCoTenants_BothGoIntoAnEmptyRole()
    {
        var fill = LeaseDraftContractFill.Merge(new LeaseDraftValues(), Reading(), LeaseOrigin.Imported, Guid.NewGuid);

        Assert.Single(fill.Values.Landlords);
        Assert.Equal([FirstTenantCode, SecondTenantCode], fill.Values.Tenants.Select(t => t.FiscalCode));
        Assert.DoesNotContain(fill.Fields, f => f.Field == "party");
    }

    [Fact]
    public void ATenantAlreadyTyped_IsKept_AndTheTenantsOfTheContractAreOnlyToldAbout()
    {
        var typed = new LeaseDraftValues { Tenants = [new LeaseDraftParty { Id = Guid.NewGuid(), FiscalCode = TypedTenantCode }] };

        var fill = LeaseDraftContractFill.Merge(typed, Reading(), LeaseOrigin.Imported, Guid.NewGuid);

        // It may be the wrong contract: nobody is added to the person who was typed, and each person the contract names is a conflict.
        Assert.Equal([TypedTenantCode], fill.Values.Tenants.Select(t => t.FiscalCode));
        Assert.Equal(2, fill.Fields.Count(f => f is { Field: "party", State: FieldReadingState.Conflict }));
    }

    [Fact]
    public void ATenantAlreadyNamedByTheContract_DoesNotBlockTheCoTenantTheSameContractNames()
    {
        var typed = new LeaseDraftValues { Tenants = [new LeaseDraftParty { Id = Guid.NewGuid(), FiscalCode = FirstTenantCode }] };

        var fill = LeaseDraftContractFill.Merge(typed, Reading(), LeaseOrigin.Imported, Guid.NewGuid);

        Assert.Equal([FirstTenantCode, SecondTenantCode], fill.Values.Tenants.Select(t => t.FiscalCode));
        Assert.Equal("Giulia", fill.Values.Tenants.Single(t => t.FiscalCode == FirstTenantCode).FirstName);
        Assert.DoesNotContain(fill.Fields, f => f is { Field: "party", State: FieldReadingState.Conflict });
    }

    /// <summary>A contract with a landlord and two tenants, each with a fiscal code, a name and a residence.</summary>
    private static LeaseContractReading Reading() =>
        new()
        {
            Parties =
            [
                Party(PartyRole.Landlord, LandlordCode, "Mario", "Rossi", "Roma (RM), Via dei Mille n. 5"),
                Party(PartyRole.Tenant, FirstTenantCode, "Giulia", "Verdi", "Milano (MI), Via Argiro n. 7"),
                Party(PartyRole.Tenant, SecondTenantCode, "Paolo", "Bianchi", "Milano (MI), Via Verdi n. 3"),
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
}
