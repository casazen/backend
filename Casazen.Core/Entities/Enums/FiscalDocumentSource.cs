namespace Casazen.Core.Entities.Enums;

/// <summary>Who issued a fiscal document (FIS-03, FIS-04, E06/F01/US02/T01).</summary>
public enum FiscalDocumentSource
{
    /// <summary>Issued by CasaZen for a direct booking.</summary>
    CasaZen = 1,

    /// <summary>Issued in Octorate and imported read-only.</summary>
    Octorate = 2,
}
