using System.ComponentModel.DataAnnotations;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of the guest session (<c>POST api/public/guest/session</c>, DB-06a): the booking site, the booking code of the confirmation e-mail and ONE
/// proof that the caller is the guest of that booking: the e-mail the booking was made with (<see cref="Email"/>, as "Le mie prenotazioni" asks it)
/// or, with the flag <c>WhiteLabelHost</c> on, the last four digits of the guest's phone (<see cref="PhoneLast4"/>, NF-143). In the body, never in a
/// URL, so neither the code, nor the address, nor the digits end up in request logs.
/// </summary>
/// <remarks>
/// <para><b>Flag off</b>: <see cref="PhoneLast4"/> is not read at all and the body is checked exactly as the lookup's
/// (<see cref="GuestBookingLookupRequest"/>), same rules and same messages, so a body with the digits and no e-mail gets the 400 of a missing
/// e-mail and nothing changes for anybody.</para>
/// <para><b>Flag on</b>: exactly one of the two. Neither, or both, is a 400 <c>validation_error</c> that says nothing about any booking; the digits
/// are four ASCII digits and nothing else (<see cref="GuestPhoneDigits.IsValidTyped"/>). A blank value counts as not given, as a blank e-mail
/// always did.</para>
/// </remarks>
public class GuestSessionRequest : IPerEmailRateLimitedRequest, IGuestBookingCodeRequest
{
    [Required(ErrorMessage = "GuestBookingOrgRequired")]
    [MaxLength(100, ErrorMessage = "GuestBookingOrgRequired")]
    public string OrgSlug { get; set; } = string.Empty;

    [Required(ErrorMessage = "GuestBookingCodeRequired")]
    [MaxLength(64, ErrorMessage = "GuestBookingCodeInvalid")]
    public string BookingCode { get; set; } = string.Empty;

    /// <summary>
    /// The e-mail the booking was made with. Empty when it is left out, as in the lookup's body, so a missing address is refused exactly as the lookup
    /// refuses it; nullable, so the phone proof needs none (a non-nullable string would be implicitly required).
    /// </summary>
    [GuestSessionProof(GuestSessionProofCheck.EmailGiven)]
    [GuestSessionProof(GuestSessionProofCheck.EmailAddress)]
    [MaxLength(255, ErrorMessage = "GuestBookingEmailInvalid")]
    public string? Email { get; set; } = string.Empty;

    /// <summary>The last four digits of the phone the guest gave when booking (NF-143). Read only with the flag <c>WhiteLabelHost</c> on.</summary>
    [GuestSessionProof(GuestSessionProofCheck.PhoneLast4)]
    public string? PhoneLast4 { get; set; }

    /// <summary>The guests' limit per e-mail counts the address over the whole site: no scope.</summary>
    string? IPerEmailRateLimitedRequest.RateLimitScope => null;

    /// <summary>
    /// True when the body proves the booking with the digits of the phone: the flag <c>WhiteLabelHost</c> is on and <see cref="PhoneLast4"/> is given.
    /// Otherwise it is the e-mail proof, as before the flag existed. No reader of the flags counts as the flag off.
    /// </summary>
    public bool UsesPhoneProof(IFeatureFlags? featureFlags) =>
        !string.IsNullOrWhiteSpace(PhoneLast4) && featureFlags?.IsEnabled(FeatureFlags.WhiteLabelHost) == true;
}

/// <summary>Which check of the proof of a <see cref="GuestSessionRequest"/> a <see cref="GuestSessionProofAttribute"/> makes.</summary>
public enum GuestSessionProofCheck
{
    /// <summary>
    /// On the e-mail: given, as the lookup's <c>[Required]</c> (its message with the flag off; with it on the message names both proofs), unless the
    /// digits are the proof: then it must not be given too.
    /// </summary>
    EmailGiven,

    /// <summary>On the e-mail: an address, as the lookup's <c>[EmailAddress]</c>, unless the digits are the proof.</summary>
    EmailAddress,

    /// <summary>On the digits: four ASCII digits when given. Not read with the flag off.</summary>
    PhoneLast4,
}

/// <summary>
/// The checks of the proof of the guest session (NF-143) on the properties of <see cref="GuestSessionRequest"/>, so they are reported together with
/// the other field errors, as the attributes of the lookup's body are. The flag is read from the services of the request (<see cref="IFeatureFlags"/>;
/// none means off). With the flag off the two checks of the e-mail are the lookup's <c>[Required]</c> and <c>[EmailAddress]</c>, with the same
/// messages, and the digits are not read.
/// </summary>
/// <remarks>MVC localizes only the built-in attributes: this one translates its key itself (<c>SharedResources</c>).</remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class GuestSessionProofAttribute(GuestSessionProofCheck check) : ValidationAttribute
{
    private static readonly EmailAddressAttribute Address = new();

    /// <summary>The check this attribute makes.</summary>
    public GuestSessionProofCheck Check { get; } = check;

    /// <summary>One identity per instance: the property carries the attribute more than once.</summary>
    public override object TypeId => this;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);
        var featureFlags = validationContext.GetService(typeof(IFeatureFlags)) as IFeatureFlags;
        var flagOn = featureFlags?.IsEnabled(FeatureFlags.WhiteLabelHost) == true;
        var phoneProof = validationContext.ObjectInstance is GuestSessionRequest request && request.UsesPhoneProof(featureFlags);
        var text = value as string;
        var blank = string.IsNullOrWhiteSpace(text);

        var key = Check switch
        {
            GuestSessionProofCheck.EmailGiven when phoneProof => blank ? null : "GuestSessionProofExclusive",
            GuestSessionProofCheck.EmailGiven when blank => flagOn ? "GuestSessionProofRequired" : "GuestBookingEmailRequired",
            GuestSessionProofCheck.EmailAddress when phoneProof || (flagOn && blank) => null,
            GuestSessionProofCheck.EmailAddress => Address.IsValid(value) ? null : "GuestBookingEmailInvalid",
            GuestSessionProofCheck.PhoneLast4 when flagOn && !blank && !GuestPhoneDigits.IsValidTyped(text) => "GuestSessionPhoneLast4Invalid",
            _ => null,
        };
        if (key is null)
            return ValidationResult.Success;

        var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
        return new ValidationResult(localizer?[key].Value ?? key, [validationContext.MemberName ?? string.Empty]);
    }
}
