namespace Casazen.Core.Services;

public sealed record WhatsAppTemplateParameter(string Value);
public sealed record WhatsAppSendResult(bool Sent, string? ProviderMessageId = null);

public interface IWhatsAppSender
{
    Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string template, string language, IReadOnlyList<WhatsAppTemplateParameter> body,
        CancellationToken cancellationToken = default);
}
