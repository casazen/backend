using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The issuers that wait for the channel to the SDI (E17/F01/US02/T02, NF-116) in the section «Italia» of the billing setup: when the channel is connected and the SDI submission is on, the
/// item <see cref="BillingSetupItemKeys.SdiIssuerRegistrationPending"/> counts the issuers whose VAT number the provider has not enabled yet (it refused a submission of theirs for that reason, and none
/// was accepted since: <see cref="IFiscalDocumentSdiService.IssuerRegistrationsAsync"/>). Nothing is asked of the user: the documents wait as «to send», or are declared sent by hand.
/// </summary>
/// <remarks>
/// It reads our own documents and never asks the provider: the setup is read by the guide again and again. It looks at the organization it is given (it may run with no tenant of its own) and returns
/// a count, never a name or a fiscal id. With no channel, or the module off, or no issuer with a VAT number, it has nothing to look at and does not make its section apply.
/// </remarks>
public sealed class SdiIssuerRegistrationContributor(
    IFiscalDocumentSdiService sdi,
    ISdiEInvoiceProvider provider,
    IFeatureFlags featureFlags) : IBillingSetupContributor
{
    public string Section => BillingSetupSections.Italy;

    // The setup of the billing is the organization's (the guide of its administrators, like every contributor of the section): the whole org is looked at, whoever reads it.
    public async Task<bool> AppliesAsync(Guid orgId, CancellationToken ct) =>
        ChannelIsOpen && (await sdi.IssuerRegistrationsAsync(new HostScope(orgId), ct)).Count > 0;

    public async Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct)
    {
        if (!ChannelIsOpen)
            return [];

        var pending = (await sdi.IssuerRegistrationsAsync(new HostScope(orgId), ct)).Count(issuer => issuer.State == SdiIssuerRegistrationStates.Pending);
        return pending == 0
            ? []
            : [new BillingSetupItem(BillingSetupItemKeys.SdiIssuerRegistrationPending, Section, BillingSetupReasons.Pending, pending)];
    }

    private bool ChannelIsOpen => provider.IsConfigured && featureFlags.IsEnabled(FeatureFlags.SdiSubmission);
}
