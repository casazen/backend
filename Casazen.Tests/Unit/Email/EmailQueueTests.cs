using System.Reflection;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>FD-13 (A4-20): emails are delivered by a Hangfire job, never inside the request.</summary>
public class EmailQueueTests
{
    private static readonly EmailContent Content = new("Oggetto", "<p>Ciao</p>");

    [Fact]
    public void Enqueue_ConfiguredProvider_CreatesDeliveryJobWithMessage()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);

        var queued = queue.Enqueue(" host@example.com ", Content, "template-x");

        Assert.True(queued);
        client.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(EmailDeliveryJob)
                    && job.Method.Name == nameof(EmailDeliveryJob.SendAsync)
                    && (string)job.Args[0] == "host@example.com"
                    && (string)job.Args[1] == Content.Subject
                    && (string)job.Args[2] == Content.HtmlBody
                    && (string)job.Args[3] == "template-x"),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public void Enqueue_ProviderNotConfigured_SkipsWithoutCreatingJob()
    {
        var client = new Mock<IBackgroundJobClient>(MockBehavior.Strict);
        var queue = new HangfireEmailQueue(
            client.Object, Options.Create(new EmailOptions()), NullLogger<HangfireEmailQueue>.Instance);

        Assert.False(queue.Enqueue("host@example.com", Content, "template-x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enqueue_NoRecipient_SkipsWithoutCreatingJob(string? to)
    {
        var client = new Mock<IBackgroundJobClient>(MockBehavior.Strict);
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);

        Assert.False(queue.Enqueue(to, Content, "template-x"));
    }

    [Fact]
    public void Enqueue_StorageFailure_ReturnsFalseInsteadOfThrowing()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));
        var queue = new HangfireEmailQueue(client.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<HangfireEmailQueue>.Instance);

        Assert.False(queue.Enqueue("host@example.com", Content, "template-x"));
    }

    [Fact]
    public async Task SendAsync_TransientProviderFailure_ThrowsSoHangfireRetries()
    {
        var job = CreateJob(new EmailSendResult(false, "RateLimitExceeded", IsTransient: true));

        await Assert.ThrowsAsync<EmailDeliveryException>(() => job.SendAsync("a@b.it", "s", "<p>h</p>", "t"));
    }

    [Theory]
    [MemberData(nameof(FinalResults))]
    public async Task SendAsync_SentSkippedOrPermanentFailure_CompletesWithoutRetry(EmailSendResult result)
    {
        var job = CreateJob(result);

        await job.SendAsync("a@b.it", "s", "<p>h</p>", "t");
    }

    public static TheoryData<EmailSendResult> FinalResults => new()
    {
        EmailSendResult.Sent(),
        EmailSendResult.NotConfigured(),
        new EmailSendResult(false, "InvalidFromAddress", IsTransient: false),
    };

    [Fact]
    public void SendAsync_RetryPolicy_DeletesJobAfterLastAttemptSoRecipientDataIsNotKept()
    {
        var retry = typeof(EmailDeliveryJob)
            .GetMethod(nameof(EmailDeliveryJob.SendAsync))!
            .GetCustomAttribute<AutomaticRetryAttribute>();

        Assert.NotNull(retry);
        Assert.Equal(EmailDeliveryJob.MaxAttempts, retry.Attempts);
        Assert.Equal(AttemptsExceededAction.Delete, retry.OnAttemptsExceeded);
    }

    private static EmailDeliveryJob CreateJob(EmailSendResult result)
    {
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(result);
        return new EmailDeliveryJob(emailService.Object, NullLogger<EmailDeliveryJob>.Instance);
    }
}
