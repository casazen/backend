using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class SelfieRequestSizeTests
{
    private const int PictureBytes = 4_000_000;

    [Fact]
    public void Selfie_HasTheCapOfFourPictures_AndTheDocumentKeepsItsOwn()
    {
        var selfie = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        var document = Limits(nameof(PublicGuestCheckInController.UploadDocument));

        Assert.Equal((long)PublicGuestCheckInController.MaxSelfieRequestBytes, selfie.Cap);
        Assert.Equal(4L * Casazen.Core.Services.CheckInDocumentLimits.MaxImageBytes + 64 * 1024, selfie.Cap);
        Assert.Equal((long)PublicGuestCheckInController.MaxImageRequestBytes, document.Cap);
        Assert.True(selfie.Form.MemoryBufferThreshold >= selfie.Cap, "the whole request is buffered in memory, never in a temporary file");
        Assert.True(selfie.Form.MultipartBodyLengthLimit >= selfie.Cap);

        Assert.True(4L * PictureBytes > PublicGuestCheckInController.MaxImageRequestBytes);
        Assert.True(4L * PictureBytes + 10_000 < selfie.Cap);
    }

    [Fact]
    public async Task Selfie_FourPicturesOfAbout4MB_Pass()
    {
        var (cap, form) = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        await using var host = await ProbeHost.StartAsync(cap, form);
        using var client = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };

        using var content = new MultipartFormDataContent();
        foreach (var field in new[] { "selfie", "turnedSelfie", "documentPhoto", "documentPhotoBack" })
        {
            var picture = new ByteArrayContent(new byte[PictureBytes]);
            picture.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(picture, field, $"{field}.jpg");
        }

        var answer = await client.PostAsync("/selfie", content);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("4", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Selfie_ARequestOverTheCap_Is413_AndTheOldCapRefusedTheSameFourPictures()
    {
        var (cap, form) = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        await using (var host = await ProbeHost.StartAsync(cap, form))
        {
            Assert.Equal(413, await host.StatusOfDeclaredBodyAsync(cap + 1));
        }

        var (oldCap, oldForm) = Limits(nameof(PublicGuestCheckInController.UploadDocument));
        await using var old = await ProbeHost.StartAsync(oldCap, oldForm);
        Assert.Equal(413, await old.StatusOfDeclaredBodyAsync((4L * PictureBytes) + 10_000));
    }

    private static (long Cap, RequestFormLimitsAttribute Form) Limits(string action)
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(action)!;
        var size = method.GetCustomAttribute<RequestSizeLimitAttribute>();
        var form = method.GetCustomAttribute<RequestFormLimitsAttribute>();

        Assert.NotNull(size);
        Assert.NotNull(form);
        return (((IRequestSizeLimitMetadata)size).MaxRequestBodySize!.Value, form);
    }

    private sealed class ProbeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ProbeHost(WebApplication app, Uri baseAddress)
        {
            _app = app;
            BaseAddress = baseAddress;
        }

        public Uri BaseAddress { get; }

        public static async Task<ProbeHost> StartAsync(long cap, RequestFormLimitsAttribute formLimits)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath(), EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.MapPost("/selfie", async (HttpContext http) =>
            {
                http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = cap;
                var options = new FormOptions
                {
                    MemoryBufferThreshold = formLimits.MemoryBufferThreshold,
                    MultipartBodyLengthLimit = formLimits.MultipartBodyLengthLimit,
                };
                http.Features.Set<IFormFeature>(new FormFeature(http.Request, options));
                var form = await http.Request.ReadFormAsync();
                return Results.Ok(form.Files.Count);
            });
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new ProbeHost(app, new Uri(address));
        }

        public async Task<int> StatusOfDeclaredBodyAsync(long declaredBytes)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, BaseAddress.Port, timeout.Token);
            await using var stream = tcp.GetStream();

            var request = "POST /selfie HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: multipart/form-data; boundary=x\r\n"
                          + $"Content-Length: {declaredBytes}\r\nConnection: close\r\n\r\n--x\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            var statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
            return int.Parse(statusLine.Split(' ')[1]);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
