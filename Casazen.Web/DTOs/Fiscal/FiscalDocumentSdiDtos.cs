using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// The SDI state of a fiscal document as a host reads it (<c>GET api/fiscal/documents/{id}/sdi</c>, E17/F01/US02/T01): the state, the code of what went wrong and its
/// reason in plain words, and what can be done now. The words are those of the request's language; the provider's name never appears (decision D14).
/// </summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Status">One of <c>to_send</c>, <c>submitted</c>, <c>delivered</c>, <c>rejected</c>, <c>manual_declared</c> (a document that does not go to the SDI is <c>not_applicable</c> or <c>unknown</c>).</param>
/// <param name="ErrorCode">The code of the rejection (the SDI's), or of the refusal of the channel that left the document «to send».</param>
/// <param name="Reason">The reason of <paramref name="ErrorCode"/> in plain words; <c>null</c> while nothing went wrong.</param>
/// <param name="StatusUpdatedAt">When the state is of (UTC); the declared day for a document declared by hand.</param>
/// <param name="ProviderConfigured">A channel to the SDI is connected: «Invia» is attempted (otherwise it answers <c>NotConfigured</c> and the XML is uploaded by hand).</param>
/// <param name="CanSubmit">The document is «to send»: it can be submitted.</param>
/// <param name="CanDeclare">The state accepts a declaration (to send, declared sent by hand).</param>
/// <param name="CanCorrect">The document was rejected: its correction can be started.</param>
public sealed record FiscalDocumentSdiDto(
    Guid DocumentId,
    string Status,
    string? ErrorCode,
    string? Reason,
    DateTime? StatusUpdatedAt,
    bool ProviderConfigured,
    bool CanSubmit,
    bool CanDeclare,
    bool CanCorrect)
{
    public static FiscalDocumentSdiDto From(FiscalDocumentSdiState state, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texts);

        // A rejected document always says why (the generic words carry the raw code); a document still «to send» says why only when the channel refused it.
        var says = state.Status == FiscalDocumentSdiStatuses.Rejected || (state.Status == FiscalDocumentSdiStatuses.ToSend && state.ErrorCode is not null);
        return new FiscalDocumentSdiDto(
            state.DocumentId,
            state.Status,
            state.ErrorCode,
            says ? SdiRejectionReasons.Describe(state.ErrorCode, texts, language) : null,
            state.StatusUpdatedAt,
            state.ProviderConfigured,
            state.CanSubmit,
            state.CanDeclare,
            state.CanCorrect);
    }
}

/// <summary>The answer of <c>POST api/fiscal/documents/{id}/sdi/submit</c>: what the submission did (<c>NotConfigured</c>, <c>Submitted</c>, <c>Refused</c>, <c>AlreadySubmitted</c>) and the state now.</summary>
public sealed record SubmitFiscalDocumentSdiResultDto(string Outcome, FiscalDocumentSdiDto State);

/// <summary>
/// <c>POST api/fiscal/documents/{id}/sdi/declare</c>: the issuer sent the file through its own channel. <c>Sent</c> carries the day (<see cref="Date"/>) and the reference of the sending;
/// <c>Rejected</c> the code (and the text) of the notification of rejection the SDI sent.
/// </summary>
public sealed record DeclareFiscalDocumentSdiRequest
{
    public SdiDeclarationKind? Kind { get; init; }

    public DateOnly? Date { get; init; }

    public string? Reference { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorText { get; init; }
}

/// <summary>One issuer that sends electronic invoices to the SDI and where it stands with the channel (<c>GET api/fiscal/sdi/issuers</c>, E17/F01/US02/T02).</summary>
/// <param name="IssuerProfileId">The issuer profile.</param>
/// <param name="PropertyOwnerId">The owner it is the profile of; <c>null</c> for the organization's own profile.</param>
/// <param name="State"><c>registered</c>, <c>pending</c> (the provider refused it: its VAT number is not enabled yet) or <c>unknown</c> (nothing was submitted yet).</param>
public sealed record SdiIssuerRegistrationDto(Guid IssuerProfileId, Guid? PropertyOwnerId, string State);

/// <summary>The channel to the SDI of this environment and its issuers (<c>GET api/fiscal/sdi/issuers</c>).</summary>
/// <param name="ProviderConfigured">A channel to the SDI is connected.</param>
/// <param name="Issuers">Each issuer with a VAT number and its state.</param>
public sealed record SdiIssuersDto(bool ProviderConfigured, IReadOnlyList<SdiIssuerRegistrationDto> Issuers);
