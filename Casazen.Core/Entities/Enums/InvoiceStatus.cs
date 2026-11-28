namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Status of an agency invoice (FatturaPA) issued to a property owner for monthly commission (ADR-019).
/// Phase 1: no automatic SDI transmission, status remains ToBeIssuedManually until agency downloads XML/PDF
/// and submits manually to SDI.
/// </summary>
public enum InvoiceStatus
{
    Draft,

    /// <summary>
    /// Invoice ready to be issued manually (Phase 1). No SDI provider configured or Swiss agency (Org.Country = "CH").
    /// Agency must download XML/PDF and submit to SDI manually.
    /// </summary>
    ToBeIssuedManually,

    Issued
}
