using Casazen.Core.Models;
using Casazen.Core.Services;
using Moq;

namespace Casazen.Tests.Unit;

public static class LegalTestServices
{
    public const string TosVersion = "2026-10-v1";

    public static ILegalDocumentService Legal(string tosVersion = TosVersion)
    {
        var legal = new Mock<ILegalDocumentService>();
        legal.Setup(l => l.GetTos()).Returns(new LegalDocumentMeta(tosVersion, null, "Terms", "Terms", null));
        return legal.Object;
    }
}
