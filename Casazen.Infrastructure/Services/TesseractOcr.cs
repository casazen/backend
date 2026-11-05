using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Casazen.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>The text reader cannot run: the library or a language file is not there, or its SHA-256 is not the pinned one (ADR-013-R8).</summary>
public sealed class DocumentReaderUnavailableException(string reasonCode) : InvalidOperationException("The document text reader is not available.")
{
    /// <summary>Why, as a code (<c>library_missing</c>, <c>hash_mismatch</c>, <c>file_missing</c>, ...).</summary>
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>Reads the characters of one line of the machine readable zone of a document (OCR-B).</summary>
public interface IMrzTextReader
{
    /// <summary>
    /// The text of the strip, which holds a single line, or null when nothing was read. The strip is read from memory: no file. Throws
    /// <see cref="DocumentReaderUnavailableException"/> when the reader cannot run at all.
    /// </summary>
    Task<string?> ReadLineAsync(GrayBitmap strip, CancellationToken cancellationToken);
}

/// <summary>Reads the printed text of a whole document page (the visual zone).</summary>
public interface IPageTextReader
{
    /// <summary>
    /// The text of the page, or null when nothing was read. The page is read from memory: no file. Throws
    /// <see cref="DocumentReaderUnavailableException"/> when the reader cannot run at all.
    /// </summary>
    Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken);
}

/// <summary>
/// The C interface of Tesseract (<c>capi.h</c>), bound at run time to the library installed in the image (the package that already serves the
/// energy certificate). Only the dozen calls the readers need. The picture goes in as raw pixels from memory: nothing is written to disk. The
/// library is not loaded until a reader is used, and its absence is a code, not a crash.
/// </summary>
internal static class TesseractNative
{
    private static readonly Lazy<Api?> Library = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The bound library, or null when no Tesseract was found on this machine.</summary>
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
                // A library with that name that is not Tesseract: try the next name.
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
    private const int PixelsPerInch = 300;

    private readonly TesseractNative.Api _api;
    private readonly string _dataPath;
    private readonly string _languages;
    private readonly int _pageSegMode;
    private readonly string? _whitelist;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<IntPtr> _idle = [];
    private readonly ConcurrentBag<IntPtr> _all = [];

    public TesseractPool(TesseractNative.Api api, string dataPath, string languages, int pageSegMode, string? whitelist, int size)
    {
        _api = api;
        _dataPath = dataPath;
        _languages = languages;
        _pageSegMode = pageSegMode;
        _whitelist = whitelist;
        _slots = new SemaphoreSlim(size, size);
        // The first engine is created now, so a language file the library cannot load is found before the first document.
        var first = CreateEngine();
        _idle.Add(first);
    }

    public async Task<string?> ReadAsync(GrayBitmap image, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        var engine = IntPtr.Zero;
        try
        {
            if (!_idle.TryTake(out engine))
                engine = CreateEngine();

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
            _api.SetSourceResolution(engine, PixelsPerInch);
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

/// <summary>
/// Creates a <see cref="TesseractPool"/> once the language files are proved (ADR-013-R8) and keeps it; a failure is remembered for five minutes,
/// so a document that arrives while the files are wrong costs one check, not one per request.
/// </summary>
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

/// <summary>
/// The OCR-B reader of the lines of the MRZ (E03/F04/US01/T02): Tesseract in process, one line at a time (page-segmentation mode 7), only the
/// characters of the MRZ allowed. The language file is an external model file with a pinned SHA-256 (ADR-013-R8); without the library or with a
/// file that is not the pinned one the reader is unavailable and the document goes to the guided manual entry.
/// </summary>
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

/// <summary>
/// The reader of the printed fields of a document (the visual zone): Tesseract in process with the Italian and English language files, whole
/// page, from memory. The files are external model files with a pinned SHA-256 (ADR-013-R8), like the OCR-B file.
/// </summary>
public sealed class TesseractPageTextReader : IPageTextReader, IDisposable
{
    private const int PageSegmentationAuto = 3;

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
                return new TesseractPool(api, folder, languages, PageSegmentationAuto, null, settings.MaxConcurrency);
            },
            logger,
            "visual-zone");
    }

    public async Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken)
    {
        var pool = await _pool.GetAsync(cancellationToken);
        return await pool.ReadAsync(page, cancellationToken);
    }

    public void Dispose() => _pool.Dispose();
}
