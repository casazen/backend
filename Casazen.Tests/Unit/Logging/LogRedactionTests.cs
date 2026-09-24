using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.External;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Resend;
using Xunit;

namespace Casazen.Tests.Unit.Logging;

/// <summary>FD-17 (A9-36): email addresses never reach the logs in clear, only as <c>m***@domain</c>.</summary>
public class LogRedactionTests
{
    private const string Email = "mario.rossi@example.it";
    private const string Masked = "m***@example.it";

    [Theory]
    [InlineData("mario.rossi@example.it", "m***@example.it")]
    [InlineData("  Anna@Studio.IT ", "A***@Studio.IT")]
    [InlineData("a@x.it", "a***@x.it")]
    [InlineData("not-an-address", "***")]
    [InlineData("@example.it", "***")]
    [InlineData("mario@", "***")]
    [InlineData("", "(none)")]
    [InlineData(null, "(none)")]
    public void MaskEmail_Value_KeepsOnlyFirstCharacterAndDomain(string? email, string expected)
    {
        Assert.Equal(expected, LogRedaction.MaskEmail(email));
    }

    [Fact]
    public void MaskEmails_TextWithAddresses_MasksEveryAddress()
    {
        var masked = LogRedaction.MaskEmails($"User with email {Email} already exists, contact admin@casazen.test");

        Assert.Equal($"User with email {Masked} already exists, contact a***@casazen.test", masked);
    }

    [Fact]
    public void MaskEmails_AlreadyMaskedText_IsUnchanged()
    {
        Assert.Equal($"invite for {Masked}", LogRedaction.MaskEmails($"invite for {Masked}"));
    }

    [Fact]
    public async Task Register_Success_LogsMaskedEmailOnly()
    {
        var logger = new CapturingLogger<AuthController>();
        var userService = new Mock<IUserService>();
        userService
            .Setup(s => s.RegisterUserAsync(Email, "Mario", "Rossi", "secret"))
            .ReturnsAsync(new User { Id = "user-1", Email = Email, FirstName = "Mario", LastName = "Rossi" });
        var controller = new AuthController(userService.Object, logger);

        var result = await controller.Register(new RegisterRequest(Email, "Mario", "Rossi", "secret"));

        Assert.IsType<OkObjectResult>(result);
        AssertMaskedOnly(logger.AllOutput);
        Assert.Contains(logger.Entries, e => e.Message.Contains(Masked, StringComparison.Ordinal));
        Assert.DoesNotContain("Rossi", logger.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_DuplicateEmail_LogsMaskedErrorMessage()
    {
        var logger = new CapturingLogger<AuthController>();
        var userService = new Mock<IUserService>();
        userService
            .Setup(s => s.RegisterUserAsync(Email, "Mario", "Rossi", "secret"))
            .ThrowsAsync(new InvalidOperationException($"User with email {Email} already exists"));
        var controller = new AuthController(userService.Object, logger);

        var result = await controller.Register(new RegisterRequest(Email, "Mario", "Rossi", "secret"));

        Assert.IsType<BadRequestObjectResult>(result);
        AssertMaskedOnly(logger.AllOutput);
        Assert.Contains(logger.Entries, e => e.Message.Contains(Masked, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InviteSupplier_Created_LogsMaskedEmailOnly()
    {
        var logger = new CapturingLogger<AdminSuppliersController>();
        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(s => s.CreateInviteAsync(Email, "015146", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierInvite(Guid.NewGuid(), DateTime.UtcNow.AddDays(7)));
        var controller = new AdminSuppliersController(supplierService.Object, logger);

        var result = await controller.InviteSupplier(
            new AdminInviteSupplierRequest { Email = Email, ComuneCode = "015146" },
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        AssertMaskedOnly(logger.AllOutput);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Masked, entry.Values["MaskedEmail"]);
    }

    [Fact]
    public async Task SendEmailAsync_ProviderErrorQuotingAnAddress_LogsAndReturnsItMasked()
    {
        var logger = new CapturingLogger<ResendEmailService>();
        var exception = new ResendException(
            HttpStatusCode.Forbidden,
            ErrorType.InvalidFromAddress,
            $"You can only send testing emails to your own email address ({Email}).",
            null);
        var resend = new Mock<IResend>();
        resend
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(exception, null));
        var service = new ResendEmailService(resend.Object, EmailTestHelpers.ConfiguredEmail(), logger);

        var result = await service.SendEmailAsync("guest.person@example.org", "Oggetto", "<p>Ciao</p>");

        Assert.False(result.Success);
        AssertMaskedOnly(logger.AllOutput);
        Assert.DoesNotContain("guest.person@example.org", logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Message.Contains(Masked, StringComparison.Ordinal));
        Assert.DoesNotContain(Email, result.ErrorDetail, StringComparison.Ordinal);
        Assert.Contains(Masked, result.ErrorDetail, StringComparison.Ordinal);
    }

    private static void AssertMaskedOnly(string logOutput)
    {
        Assert.NotEmpty(logOutput);
        Assert.DoesNotContain(Email, logOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mario.rossi", logOutput, StringComparison.OrdinalIgnoreCase);
    }
}
