using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The list of the owners of an agency with what the screen «Proprietari» shows of each (E13/F02/US01/T01, FIX/RES-OWNERS): the person, how many properties the owner
/// holds, the kind and the settlement currency of its fiscal profile and the mandate in force today, in <b>one query</b> for the whole list (never one read for each row).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Reach.</b> The web layer decides the permission (<c>agency.owner.view</c>) and passes the caller's <see cref="HostScope"/>; the service applies it. An org-wide
/// scope reads every owner of the org; a partial one (a collaborator «Solo alcuni», an account outside the org team) only the owners the mandate endpoints would let it
/// open: the owner holds at least one ownership period and every one of them is on a property the scope reaches. An owner with no period is in no partial reach (the same
/// definition as <see cref="IOwnerMandateService"/>, so the list never shows an owner whose mandate answers 404). Another org's owners are never read.</item>
/// <item><b>Today</b> is the Rome day of the injected clock: the ownership not ended, the fiscal profile and the mandate in force are those of that day.</item>
/// <item><b>Personal data.</b> What the registry list already carried (the name, the e-mail, the phone, the tax regime, the IBAN that the web layer masks) and nothing
/// of the fiscal profile but its kind and its currency: no identifier, no address, no bank account is read for the list.</item>
/// </list>
/// </remarks>
public interface IOwnerDirectoryService
{
    /// <summary>The owners of the org of <paramref name="scope"/> inside its reach, by last name and first name.</summary>
    Task<IReadOnlyList<OwnerDirectoryRow>> ListAsync(HostScope scope, CancellationToken ct = default);
}

/// <summary>One owner of the list.</summary>
/// <param name="Iban">The clear IBAN of the registry: it never leaves the web layer, which masks it to its last four characters; never logged, never serialized as it is.</param>
/// <param name="PropertyCount">The properties the owner holds today or will hold (an ownership not ended, on a property that still exists), each counted once.</param>
/// <param name="EntityKind">The kind of the fiscal profile in force today (the newest when none is in force, as the page of the owner reads it); <c>null</c> for an owner with no profile.</param>
/// <param name="SettlementCurrency">The settlement currency of that profile; <c>null</c> when the profile has none or the owner has no profile.</param>
/// <param name="MandateInForce">The mandate of the owner in force today; <c>null</c> when there is none (never a commission of zero).</param>
public sealed record OwnerDirectoryRow(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    OwnerTaxRegime TaxRegime,
    string? Iban,
    DateTime CreatedAt,
    int PropertyCount,
    OwnerEntityKind? EntityKind,
    string? SettlementCurrency,
    OwnerDirectoryMandate? MandateInForce);

/// <summary>The terms of the mandate in force that the list shows: the commission, what it is a percentage of, whether the VAT is in it, and its days.</summary>
public sealed record OwnerDirectoryMandate(
    Guid Id,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal CommissionPercent,
    CommissionBase CommissionBase,
    bool CommissionIncludesVat);
