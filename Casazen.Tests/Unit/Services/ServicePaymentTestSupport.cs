using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Xunit;

namespace Casazen.Tests.Unit.Services;

internal static class ServicePaymentTestSupport
{
    private static readonly Regex LinkPattern = new(
        @"/service/pay/(?<id>[0-9a-f-]{36})\?token=(?<token>[A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    public static List<(string? To, EmailContent Content, string Template)> RequestEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServicePaymentRequest).ToList();

    public static List<(string? To, EmailContent Content, string Template)> ReminderEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServicePaymentReminder).ToList();

    public static List<(string? To, EmailContent Content, string Template)> PaymentEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template.StartsWith("service-payment-", StringComparison.Ordinal)).ToList();

    public static (Guid PaymentId, string Token) LinkOf(EmailContent email)
    {
        var match = LinkPattern.Match(email.HtmlBody);
        if (!match.Success)
            throw new InvalidOperationException("The email carries no payment link.");

        return (Guid.Parse(match.Groups["id"].Value), match.Groups["token"].Value);
    }

    public static async Task<ServiceRequestPayment> OnlyPaymentOfAsync(this ServiceRequestScenario scenario, Guid requestId) =>
        Assert.Single(await scenario.PaymentsOfAsync(requestId));
}
