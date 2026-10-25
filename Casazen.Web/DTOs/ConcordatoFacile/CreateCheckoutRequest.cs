namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Request body for creating a ConcordatoFacile checkout session.
/// The lease ID comes from the route, so this is empty but required for POST endpoints.
/// </summary>
public class CreateCheckoutRequest
{
    // Empty - leaseId comes from route parameter
}
