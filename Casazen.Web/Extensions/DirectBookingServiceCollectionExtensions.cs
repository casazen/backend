using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

/// <summary>
/// The direct booking of the booking site: the requests to pay at the property, the checkout outcome, the guest session and lookup, the guest account
/// bookings, the reviews, the guest messages, the host contact and the notification channels.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class DirectBookingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenDirectBookingServices(this IServiceCollection services)
    {
        // "Pay at the property" requests approved by the host (BK-06, D5).
        services.AddScoped<OnSiteRequestNotifier>();
        services.AddScoped<IOnSiteBookingRequestService, OnSiteBookingRequestService>();
        // Outcome page of the public checkout, read with the checkout token (BK-07, A3-15).
        services.AddScoped<ICheckoutOutcomeService, CheckoutOutcomeService>();
        // One instance per request for both: the session service reuses the proof and the view of "Le mie prenotazioni" (DB-06a).
        services.AddScoped<GuestBookingLookupService>();
        services.AddScoped<IGuestBookingLookupService>(sp => sp.GetRequiredService<GuestBookingLookupService>());
        // The guest's session with no account: code + email once, then a signed token (DB-06a).
        // The tokens are sealed with Data Protection (the key ring in the database), so every replica reads them.
        services.AddSingleton<GuestSessionTokens>();
        services.AddScoped<IGuestSessionService, GuestSessionService>();
        // The lock of a booking code after repeated failures of the last four digits of the phone, the other proof of the guest session (NF-143,
        // flag WhiteLabelHost): its two settings are checked at startup, its counters live in memory, per replica.
        services.AddOptions<GuestPhoneLookupLockoutOptions>()
            .Configure<IConfiguration>(GuestPhoneLookupLockoutOptions.Read)
            .Validate(
                options => options.IsValid(),
                $"{GuestPhoneLookupLockoutOptions.MaxFailuresKey} and {GuestPhoneLookupLockoutOptions.WindowMinutesKey} must be at least 1.")
            .ValidateOnStart();
        services.AddSingleton<GuestPhoneLookupLockout>();
        // The bookings of the guest who has an account (the mobile app, GuestAccountController): the direct bookings of any site whose guest
        // has the account's verified email, and the same guest-area session opened for them.
        services.AddScoped<IGuestAccountBookingService, GuestAccountBookingService>();
        // Verified reviews of the stays (DB-12a): the public reads and the guest's review, the host's
        // answers, reports and invitations, and the staff's decisions. The signed link is made by Data Protection, nothing is stored for it.
        services.AddSingleton<ReviewLinkTokens>();
        services.AddScoped<IPublicReviewService, PublicReviewService>();
        services.AddScoped<IHostReviewService, HostReviewService>();
        services.AddScoped<IAdminReviewService, AdminReviewService>();
        // Messages between the guest of a direct booking and the host (DB-14a): the thread, its two sides and the two emails
        // that are the consequence of a message.
        services.AddScoped<IGuestMessageService, GuestMessageService>();
        // «Scrivi a {host}» of the booking site: the visitor's message as one email to the host, answered from their mailbox (DB-03b).
        services.AddScoped<IHostContactService, HostContactService>();

        return services;
    }
}
