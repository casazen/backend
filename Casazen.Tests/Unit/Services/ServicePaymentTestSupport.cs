using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>Small helpers of the SP-15a unit tests: what a payment email carries, and the payment of a request.</summary>
internal static class ServicePaymentTestSupport
{
    private static readonly Regex LinkPattern = new(
        @"/service/pay/(?<id>[0-9a-f-]{36})\?token=(?<token>[A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    /// <summary>The payment request emails queued so far (the first request and its reminders, not the reminder template).</summary>
    public static List<(string? To, EmailContent Content, string Template)> RequestEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServicePaymentRequest).ToList();

    /// <summary>The reminder emails queued so far.</summary>
    public static List<(string? To, EmailContent Content, string Template)> ReminderEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServicePaymentReminder).ToList();

    /// <summary>Every email of the payment of a service, whatever the kind (request, reminder, receipt, offline notice).</summary>
    public static List<(string? To, EmailContent Content, string Template)> PaymentEmails(this RecordingEmailQueue emails) =>
        emails.Snapshot().Where(e => e.Template.StartsWith("service-payment-", StringComparison.Ordinal)).ToList();

    /// <summary>The payment id and the raw token of the link an email carries.</summary>
    public static (Guid PaymentId, string Token) LinkOf(EmailContent email)
    {
        var match = LinkPattern.Match(email.HtmlBody);
        if (!match.Success)
            throw new InvalidOperationException("The email carries no payment link.");

        return (Guid.Parse(match.Groups["id"].Value), match.Groups["token"].Value);
    }

    /// <summary>The only payment of a request, as saved.</summary>
    public static async Task<ServiceRequestPayment> OnlyPaymentOfAsync(this ServiceRequestScenario scenario, Guid requestId) =>
        Assert.Single(await scenario.PaymentsOfAsync(requestId));
}
