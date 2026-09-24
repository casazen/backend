namespace Casazen.Core.Entities.Enums;

/// <summary>How a party signs the lease contract (LT-02, D15).</summary>
public enum LeaseSignatureMethod
{
    /// <summary>
    /// Outside CasaZen: on paper or with the party's own digital signature; the landlord uploads the PDF signed by
    /// every party and declares the stipula date. The default path.
    /// </summary>
    Offline,

    /// <summary>Through the e-signature provider (<c>Features:ESignProvider</c>), with a personal signing link.</summary>
    Provider,
}
