using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CommercialSignupTests
{
    [Fact]
    public void Agency_LegacyRoomCount_PreservesSavedDraftFingerprint()
    {
        var selection = new CommercialSignup { Profile = "agency", AgencyName = "Demo Agency", PlanTier = "Agency", Rooms = 3, Addons = ["channels"] };
        Assert.Null(selection.Validate());
        Assert.Equal(3, selection.Rooms);
        Assert.Equal("ShortTerm", selection.RentalType);
        var restored = CommercialSignup.Read(selection.Serialize());
        Assert.NotNull(restored);
        Assert.Equal(selection.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public void LegacyCheckout_RetainsSerializedFormUsedForStripeFingerprint()
    {
        const string persisted = """{"Profile":"long-rent","PlanTier":"Pro","Rooms":1,"AgencyName":null,"Addons":["fiscal"]}""";
        Assert.Equal(persisted, CommercialSignup.Read(persisted)!.Serialize());
    }

    [Theory]
    [InlineData("LongTerm", false)]
    [InlineData("ShortTerm", true)]
    [InlineData("Both", true)]
    [InlineData("unknown", false)]
    public void SelectedArea_DeterminesEligibleExtras(string area, bool allowsChannels)
    {
        var signup = new CommercialSignup { RentalArea = area, PlanTier = "Pro", Addons = ["channels"] };
        Assert.Equal(allowsChannels, signup.Validate() is null);
        Assert.Equal(area, CommercialSignup.Read(signup.Serialize())!.RentalType);
    }

    [Fact]
    public void NewAgency_NeedsItsOwnName()
    {
        Assert.NotNull(new CommercialSignup { Profile = "agency", PlanTier = "Agency" }.Validate());
        Assert.NotNull(new CommercialSignup { Profile = "agency", PlanTier = "Agency", AgencyName = new string('x', 201) }.Validate());
    }

    [Fact]
    public void Starter_CannotBuyExtras_EvenIfClientSkipsUiLock()
    {
        Assert.NotNull(new CommercialSignup { Addons = ["operations"] }.Validate());
        Assert.Null(new CommercialSignup { PlanTier = "Pro", Addons = ["operations"] }.Validate());
    }

    [Theory]
    [InlineData("agency", "Pro", 3)]
    [InlineData("owner", "Agency", 1)]
    [InlineData("owner", "Pro", 3)]
    [InlineData("agency", "Agency", 0)]
    [InlineData("agency", "Agency", 10001)]
    [InlineData("supplier", "Starter", 1)]
    [InlineData("both", "Pro", 1)]
    public void InvalidProfilesOrQuantities_AreRefused(string profile, string tier, int rooms) =>
        Assert.NotNull(new CommercialSignup { Profile = profile, PlanTier = tier, Rooms = rooms }.Validate());

    [Fact]
    public void LongRent_IsExclusive_AndRejectsShortRentAddons()
    {
        var selection = new CommercialSignup { Profile = "long-rent", PlanTier = "Pro" };
        Assert.Equal("LongTerm", selection.RentalType);
        Assert.NotNull((selection with { Addons = ["channels"] }).Validate());
        Assert.Null((selection with { Addons = ["fiscal"] }).Validate());
    }

    [Fact]
    public void UnknownOrDuplicatedAddons_AreRefused()
    {
        var selection = new CommercialSignup { PlanTier = "Pro" };
        Assert.NotNull((selection with { Addons = ["operations", "operations"] }).Validate());
        Assert.NotNull((selection with { Addons = ["invented"] }).Validate());
    }
}
