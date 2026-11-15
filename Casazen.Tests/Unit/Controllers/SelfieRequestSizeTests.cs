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

/// <summary>
/// E22/F01/US01/T02: the selfie of a property in Ticino, which keeps no document, carries three pictures (the selfie and the two sides of the document that were
/// read), and the 10 MB that the request had for the two of before refused them: a guest with pictures of about 4 MB each got a 413 and could not finish. The
/// request of <c>/selfie</c> is 15 MB now, the same 5 MB for each picture. The proof runs a real Kestrel on the loopback, with the limits that the action itself
/// declares (<see cref="RequestSizeLimitAttribute"/> and <see cref="RequestFormLimitsAttribute"/>, read from <c>UploadSelfie</c>): three pictures of about 4 MB
/// pass, and a request that is over the cap is refused with 413, as the old cap refused the same three pictures.
/// </summary>
public class SelfieRequestSizeTests
{
    private const int PictureBytes = 4_000_000;

    [Fact]
    public void Selfie_HasTheCapOfThreePictures_AndTheDocumentKeepsItsOwn()
    {
        var selfie = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        var document = Limits(nameof(PublicGuestCheckInController.UploadDocument));

        Assert.Equal((long)PublicGuestCheckInController.MaxSelfieRequestBytes, selfie.Cap);
        Assert.Equal(3L * (PublicGuestCheckInController.MaxImageRequestBytes / 2), selfie.Cap);
        Assert.Equal((long)PublicGuestCheckInController.MaxImageRequestBytes, document.Cap);
        Assert.True(selfie.Form.MemoryBufferThreshold >= selfie.Cap, "the whole request is buffered in memory, never in a temporary file");
        Assert.True(selfie.Form.MultipartBodyLengthLimit >= selfie.Cap);

        // The three pictures of the proof did not fit the old cap, and fit the new one with the framing of the form.
        Assert.True(3L * PictureBytes > PublicGuestCheckInController.MaxImageRequestBytes);
        Assert.True(3L * PictureBytes + 10_000 < selfie.Cap);
    }

    [Fact]
    public async Task Selfie_ThreePicturesOfAbout4MB_Pass()
    {
        var (cap, form) = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        await using var host = await ProbeHost.StartAsync(cap, form);
        using var client = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };

        using var content = new MultipartFormDataContent();
        foreach (var field in new[] { "selfie", "documentPhoto", "documentPhotoBack" })
        {
            var picture = new ByteArrayContent(new byte[PictureBytes]);
            picture.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(picture, field, $"{field}.jpg");
        }

        var answer = await client.PostAsync("/selfie", content);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("3", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Selfie_ARequestOverTheCap_Is413_AndTheOldCapRefusedTheSameThreePictures()
    {
        var (cap, form) = Limits(nameof(PublicGuestCheckInController.UploadSelfie));
        await using (var host = await ProbeHost.StartAsync(cap, form))
        {
            // One byte over the cap is refused before a picture is read.
            Assert.Equal(413, await host.StatusOfDeclaredBodyAsync(cap + 1));
        }

        // The cap of before, 10 MB for the whole request: the size of the same three pictures (about 12 MB) was a 413.
        var (oldCap, oldForm) = Limits(nameof(PublicGuestCheckInController.UploadDocument));
        await using var old = await ProbeHost.StartAsync(oldCap, oldForm);
        Assert.Equal(413, await old.StatusOfDeclaredBodyAsync((3L * PictureBytes) + 10_000));
    }

    /// <summary>The cap and the form limits that an action declares, as the server will apply them.</summary>
    private static (long Cap, RequestFormLimitsAttribute Form) Limits(string action)
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(action)!;
        var size = method.GetCustomAttribute<RequestSizeLimitAttribute>();
        var form = method.GetCustomAttribute<RequestFormLimitsAttribute>();

        Assert.NotNull(size);
        Assert.NotNull(form);
        return (((IRequestSizeLimitMetadata)size).MaxRequestBodySize!.Value, form);
    }

    /// <summary>
    /// A Kestrel on the loopback with one endpoint that does what <c>[RequestSizeLimit]</c> and <c>[RequestFormLimits]</c> do for the action: it puts the limits
    /// of the action on the request and reads the multipart form, answering the number of files.
    /// </summary>
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

        /// <summary>
        /// The status of a form POST that declares <paramref name="declaredBytes"/> in its <c>Content-Length</c>, written by hand: a server that refuses a body for
        /// its size answers before the body is read and closes the connection, so the answer is read from the socket and not from a client that is still writing.
        /// </summary>
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
