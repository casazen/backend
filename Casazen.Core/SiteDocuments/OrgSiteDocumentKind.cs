namespace Casazen.Core.SiteDocuments;

/// <summary>
/// Which operator document of the public booking site (BK-14, A3-21). These are the host's own texts, shown on
/// <c>/book/{slug}/privacy</c> and <c>/book/{slug}/termini</c>; the CasaZen documents are <c>/legale/*</c> (PL-14).
/// </summary>
public enum OrgSiteDocumentKind
{
    /// <summary>The operator's privacy notice for guests (the data controller of the booking is the operator).</summary>
    Privacy = 0,

    /// <summary>The operator's booking terms and conditions.</summary>
    Terms = 1,
}

/// <summary>How the operator provides a document.</summary>
public enum OrgSiteDocumentSource
{
    /// <summary>The text is written in CasaZen (plain text with a few Markdown marks) and shown on the site.</summary>
    Text = 0,

    /// <summary>The document is hosted elsewhere: the site links to its https address.</summary>
    ExternalUrl = 1,
}
