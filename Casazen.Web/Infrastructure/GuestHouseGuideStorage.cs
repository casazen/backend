using Casazen.Core.GuestPortal;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Web.Infrastructure;

public sealed class GuestHouseGuideStorage(IDataProtectionProvider protection)
{
    public string Protect(Guid propertyId, GuestHouseGuide guide) => Protector(propertyId).Protect(guide.Write());

    public GuestHouseGuide Read(Guid propertyId, string? value) =>
        value is null ? new GuestHouseGuide() : GuestHouseGuide.Read(Protector(propertyId).Unprotect(value));

    private IDataProtector Protector(Guid propertyId) =>
        protection.CreateProtector("Casazen.GuestHouseGuide").CreateProtector(propertyId.ToString("D"));
}
