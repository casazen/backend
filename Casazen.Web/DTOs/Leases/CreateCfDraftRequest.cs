using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.Dtos.Leases;

/// <summary>
/// Request to create a minimal ConcordatoFacile draft: only the property, the rest filled with defaults (ADR-004).
/// The endpoint is idempotent: if a CF draft for the property already exists in Draft status, it is returned.
/// </summary>
public sealed class CreateCfDraftRequest
{
    [Required]
    public Guid PropertyId { get; set; }
}
