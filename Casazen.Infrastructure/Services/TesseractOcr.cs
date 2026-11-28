using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Casazen.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed class DocumentReaderUnavailableException(string reasonCode) : InvalidOperationException("The document text reader is not available.")
{
    public string ReasonCode { get; } = reasonCode;
}

public interface IMrzTextReader
{
    Task<string?> ReadLineAsync(GrayBitmap strip, CancellationToken cancellationToken);
}

public interface IPageTextReader
{
    Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken);

    Task<string?> ReadLayoutAsync(GrayBitmap page, bool sparse, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

internal static class TesseractNative
{
    private static readonly Lazy<Api?> Library = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Api? Get() => Library.Value;

    private static Api? Load()
    {
        foreach (var name in CandidateNames())
        {
            if (!NativeLibrary.TryLoad(name, out var handle))
                continue;

            try
            {
                return new Api(
                    Bind<Create>(handle, "TessBaseAPICreate"),
                    Bind<Delete>(handle, "TessBaseAPIDelete"),
                    Bind<Init3>(handle, "TessBaseAPIInit3"),
                    Bind<SetVariable>(handle, "TessBaseAPISetVariable"),
                    Bind<SetPageSegMode>(handle, "TessBaseAPISetPageSegMode"),
                    Bind<SetSourceResolution>(handle, "TessBaseAPISetSourceResolution"),
                    Bind<SetImage>(handle, "TessBaseAPISetImage"),
                    Bind<GetUtf8Text>(handle, "TessBaseAPIGetUTF8Text"),
                    Bind<Clear>(handle, "TessBaseAPIClear"),
                    Bind<End>(handle, "TessBaseAPIEnd"),
                    Bind<DeleteText>(handle, "TessDeleteText"));
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
            return ["tesseract55.dll", "tesseract54.dll", "tesseract53.dll", "libtesseract-5.dll", "tesseract50.dll", "tesseract.dll"];

        if (OperatingSystem.IsMacOS())
            return ["libtesseract.5.dylib", "libtesseract.dylib", "/opt/homebrew/lib/libtesseract.dylib", "/usr/local/lib/libtesseract.dylib"];

        return ["libtesseract.so.5", "libtesseract.so.4", "libtesseract.so", "libtesseract.so.5.5.0", "libtesseract.so.5.3.4"];
    }

    private static T Bind<T>(IntPtr library, string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, export));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr Create();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Delete(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Init3(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dataPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string language);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetVariable(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetPageSegMode(IntPtr handle, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetSourceResolution(IntPtr handle, int pixelsPerInch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetImage(IntPtr handle, IntPtr data, int width, int height, int bytesPerPixel, int bytesPerLine);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr GetUtf8Text(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Clear(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void End(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DeleteText(IntPtr text);

    internal sealed record Api(
        Create Create,
        Delete Delete,
        Init3 Init3,
        SetVariable SetVariable,
        SetPageSegMode SetPageSegMode,
        SetSourceResolution SetSourceResolution,
        SetImage SetImage,
        GetUtf8Text GetUtf8Text,
        Clear Clear,
        End End,
        DeleteText DeleteText);
}

/// <summary>
/// A few Tesseract engines of one language set and one page-segmentation mode, one per concurrent reading (an engine is not thread safe). The
/// picture is copied into unmanaged memory for the call, and that copy is zeroed before it is freed (ADR-013-R2, R6).
/// </summary>
internal sealed class TesseractPool : IDisposable
{
    private const int DefaultPixelsPerInch = 300;

    private readonly TesseractNative.Api _api;
    private readonly string _dataPath;
    private readonly string _languages;
    private readonly int _pageSegMode;
    private readonly int _pixelsPerInch;
    private readonly string? _whitelist;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<IntPtr> _idle = [];
    private readonly ConcurrentBag<IntPtr> _all = [];

    public TesseractPool(TesseractNative.Api api, string dataPath, string languages, int pageSegMode, string? whitelist, int size,
        int pixelsPerInch = DefaultPixelsPerInch)
    {
        _api = api;
        _dataPath = dataPath;
        _languages = languages;
        _pageSegMode = pageSegMode;
        _pixelsPerInch = pixelsPerInch;
        _whitelist = whitelist;
        _slots = new SemaphoreSlim(size, size);
        var first = CreateEngine();
        _idle.Add(first);
    }

    public async Task<string?> ReadAsync(GrayBitmap image, CancellationToken cancellationToken, int? segmentation = null)
    {
        await _slots.WaitAsync(cancellationToken);
        var engine = IntPtr.Zero;
        try
        {
            if (!_idle.TryTake(out engine))
                engine = CreateEngine();

            _api.SetPageSegMode(engine, segmentation ?? _pageSegMode);
            var handle = engine;
            return await Task.Run(() => Recognize(handle, image), CancellationToken.None);
        }
        finally
        {
            if (engine != IntPtr.Zero)
                _idle.Add(engine);

            _slots.Release();
        }
    }

    public void Dispose()
    {
        foreach (var engine in _all)
        {
            _api.End(engine);
            _api.Delete(engine);
        }

        _all.Clear();
        _idle.Clear();
        _slots.Dispose();
    }

    private IntPtr CreateEngine()
    {
        var engine = _api.Create();
        if (engine == IntPtr.Zero)
            throw new DocumentReaderUnavailableException("library_failed");

        if (_api.Init3(engine, _dataPath, _languages) != 0)
        {
            _api.Delete(engine);
            throw new DocumentReaderUnavailableException("language_load_failed");
        }

        _api.SetPageSegMode(engine, _pageSegMode);
        if (_whitelist is not null)
            _api.SetVariable(engine, "tessedit_char_whitelist", _whitelist);

        _all.Add(engine);
        return engine;
    }

    private string? Recognize(IntPtr engine, GrayBitmap image)
    {
        var length = image.Width * image.Height;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(image.Pixels, 0, buffer, length);
            _api.SetImage(engine, buffer, image.Width, image.Height, 1, image.Width);
            _api.SetSourceResolution(engine, _pixelsPerInch);
            var text = _api.GetUtf8Text(engine);
            if (text == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(text);
            }
            finally
            {
                _api.DeleteText(text);
            }
        }
        finally
        {
            _api.Clear(engine);
            var zeros = new byte[length];
            Marshal.Copy(zeros, 0, buffer, length);
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed class LazyTesseractPool(
    Func<CancellationToken, Task<TesseractPool>> create,
    ILogger logger,
    string name) : IDisposable
{
    private static readonly long RetryAfterMilliseconds = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TesseractPool? _pool;
    private string? _failure;
    private long _failedAt;

    public async Task<TesseractPool> GetAsync(CancellationToken cancellationToken)
    {
        if (_pool is { } ready)
            return ready;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pool is { } created)
                return created;

            if (_failure is not null && Environment.TickCount64 - _failedAt < RetryAfterMilliseconds)
                throw new DocumentReaderUnavailableException(_failure);

            try
            {
                _pool = await create(cancellationToken);
                _failure = null;
                logger.LogInformation("Document text reader {Reader} is ready", name);
                return _pool;
            }
            catch (DocumentReaderUnavailableException ex)
            {
                _failure = ex.ReasonCode;
                _failedAt = Environment.TickCount64;
                logger.LogWarning("Document text reader {Reader} is not available ({Reason})", name, ex.ReasonCode);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _pool?.Dispose();
        _gate.Dispose();
    }
}

public sealed class TesseractMrzTextReader : IMrzTextReader, IDisposable
{
    private const int PageSegmentationSingleLine = 7;
    private const string Whitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<";

    private readonly LazyTesseractPool _pool;

    public TesseractMrzTextReader(
        IOptions<MrzOcrOptions> options,
        ILogger<TesseractMrzTextReader> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _pool = new LazyTesseractPool(
            async token =>
            {
                var settings = options.Value;
                var api = TesseractNative.Get() ?? throw new DocumentReaderUnavailableException("library_missing");
                Func<HttpClient>? http = httpClientFactory is null ? null : () => httpClientFactory.CreateClient(BiometricModelStore.HttpClientName);
                var check = await ModelFileProvisioner.EnsureAsync(settings.Mrz, BaseDirectory, http, logger, token);
                if (!check.IsVerified)
                    throw new DocumentReaderUnavailableException(check.Failure!);

                if (!string.Equals(
                        System.IO.Path.GetFileName(check.Path),
                        settings.MrzLanguage + ".traineddata",
                        StringComparison.OrdinalIgnoreCase))
                    throw new DocumentReaderUnavailableException("language_file_mismatch");

                return new TesseractPool(
                    api,
                    System.IO.Path.GetDirectoryName(check.Path)!,
                    settings.MrzLanguage,
                    PageSegmentationSingleLine,
                    Whitelist,
                    settings.MaxConcurrency);
            },
            logger,
            "mrz");
    }

    /// <summary>The folder relative model paths start from; the application folder unless a test says otherwise.</summary>
    internal static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public async Task<string?> ReadLineAsync(GrayBitmap strip, CancellationToken cancellationToken)
    {
        var pool = await _pool.GetAsync(cancellationToken);
        return await pool.ReadAsync(strip, cancellationToken);
    }

    public void Dispose() => _pool.Dispose();
}

public sealed class TesseractPageTextReader : IPageTextReader, IDisposable
{
    private const int PageSegmentationAuto = 3;
    private const int PhotoPixelsPerInch = 96;

    private readonly LazyTesseractPool _pool;

    public TesseractPageTextReader(
        IOptions<MrzOcrOptions> options,
        ILogger<TesseractPageTextReader> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _pool = new LazyTesseractPool(
            async token =>
            {
                var settings = options.Value;
                var api = TesseractNative.Get() ?? throw new DocumentReaderUnavailableException("library_missing");
                Func<HttpClient>? http = httpClientFactory is null ? null : () => httpClientFactory.CreateClient(BiometricModelStore.HttpClientName);
                var italian = await ModelFileProvisioner.EnsureAsync(settings.VisualZoneItalian, TesseractMrzTextReader.BaseDirectory, http, logger, token);
                var english = await ModelFileProvisioner.EnsureAsync(settings.VisualZoneEnglish, TesseractMrzTextReader.BaseDirectory, http, logger, token);
                if (!italian.IsVerified)
                    throw new DocumentReaderUnavailableException(italian.Failure!);

                if (!english.IsVerified)
                    throw new DocumentReaderUnavailableException(english.Failure!);

                var folder = System.IO.Path.GetDirectoryName(italian.Path)!;
                if (!string.Equals(folder, System.IO.Path.GetDirectoryName(english.Path), StringComparison.Ordinal))
                    throw new DocumentReaderUnavailableException("language_folder_mismatch");

                var languages = string.Join(
                    "+",
                    System.IO.Path.GetFileNameWithoutExtension(italian.Path),
                    System.IO.Path.GetFileNameWithoutExtension(english.Path));
                return new TesseractPool(api, folder, languages, PageSegmentationAuto, null, settings.MaxConcurrency, PhotoPixelsPerInch);
            },
            logger,
            "visual-zone");
    }

    public async Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken)
    {
        var pool = await _pool.GetAsync(cancellationToken);
        return await pool.ReadAsync(page, cancellationToken);
    }

    public async Task<string?> ReadLayoutAsync(GrayBitmap page, bool sparse, CancellationToken cancellationToken)
    {
        var pool = await _pool.GetAsync(cancellationToken);
        return await pool.ReadAsync(page, cancellationToken, sparse ? 11 : 6);
    }

    public void Dispose() => _pool.Dispose();
}
