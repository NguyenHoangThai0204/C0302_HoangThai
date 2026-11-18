using System.Reflection;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Win32.SafeHandles;

namespace C0302_HoangThai.Service.S0302;

public sealed class PdfOcrUnavailableException(Exception inner) : Exception("OCR native libraries or language data are unavailable.", inner);

public sealed class PdfOcr : IDisposable
{
    private readonly Lazy<TesseractHandle> engine;
    private readonly Lazy<TesseractHandle?> orientationEngine;

    public PdfOcr(IConfiguration configuration)
    {
        string dataDirectory = configuration["Ocr:TessDataPath"] ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        engine = new(() => Initialize(dataDirectory));
        orientationEngine = new(() => InitializeOrientationEngine(dataDirectory));
    }

    private static TesseractHandle? InitializeOrientationEngine(string dataDirectory)
    {
        if (!File.Exists(Path.Combine(dataDirectory, "osd.traineddata")))
            return null;

        var handle = TesseractNative.Create();
        if (!handle.IsInvalid && TesseractNative.Init(handle, dataDirectory, "osd", mode: 0) == 0)
            return handle;

        handle.Dispose();
        return null;
    }

    internal int? DetectCorrection(byte[] pixels, int width, int height)
    {
        // Initialize the recognition engine first so native loading failures use the usual OCR error path.
        _ = engine.Value;
        if (orientationEngine.Value is not { } handle)
            return null;

        TesseractNative.SetPageSegMode(handle, mode: 0);
        TesseractNative.SetImage(handle, pixels, width, height, bytesPerPixel: 1, stride: width);
        TesseractNative.SetSourceResolution(handle, 250);
        try
        {
            return TesseractNative.DetectOrientation(handle, out int degrees, out float confidence, out _, out _) != 0 && confidence > 0
                ? (360 - degrees) % 360 / 90
                : null;
        }
        finally
        {
            TesseractNative.Clear(handle);
        }
    }

    public void Validate()
    {
        _ = engine.Value;
        _ = OpenPdfium();
    }

    public PdfOcrDocument Open(string path) => new(this, path);

    internal static IDocLib OpenPdfium()
    {
        try
        {
            return DocLib.Instance;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new PdfOcrUnavailableException(ex);
        }
    }

    private static TesseractHandle Initialize(string dataDirectory)
    {
        try
        {
            if (!File.Exists(Path.Combine(dataDirectory, "eng.traineddata")))
                throw new FileNotFoundException("eng.traineddata was not found.", dataDirectory);

            string language = File.Exists(Path.Combine(dataDirectory, "vie.traineddata")) ? "eng+vie" : "eng";
            var handle = TesseractNative.Create();
            if (!handle.IsInvalid && TesseractNative.Init(handle, dataDirectory, language, mode: 1) == 0)
                return handle;

            handle.Dispose();
            throw new InvalidOperationException("Tesseract initialization failed.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or BadImageFormatException
            or EntryPointNotFoundException or InvalidOperationException or TypeInitializationException)
        {
            throw new PdfOcrUnavailableException(ex);
        }
    }

    // The single PDF worker owns all recognition calls for this engine.
    internal (string Text, int Confidence) Recognize(byte[] pixels, int width, int height)
    {
        var handle = engine.Value;
        TesseractNative.SetPageSegMode(handle, mode: 3);
        TesseractNative.SetImage(handle, pixels, width, height, bytesPerPixel: 1, stride: width);
        TesseractNative.SetSourceResolution(handle, 250);
        nint text = 0;
        try
        {
            text = TesseractNative.GetUtf8Text(handle);
            if (text == 0)
                throw new InvalidOperationException("Tesseract recognition failed.");

            return (Marshal.PtrToStringUTF8(text) ?? "", TesseractNative.MeanTextConfidence(handle));
        }
        finally
        {
            if (text != 0)
                TesseractNative.DeleteText(text);
            TesseractNative.Clear(handle);
        }
    }

    public void Dispose()
    {
        if (engine.IsValueCreated)
            engine.Value.Dispose();
        if (orientationEngine.IsValueCreated)
            orientationEngine.Value?.Dispose();
    }
}

public sealed class PdfOcrDocument(PdfOcr ocr, string path) : IDisposable
{
    private IDocReader? renderer;
    private int? lastAcceptedCorrection;

    public static string ReadText(PdfDocument document, int pageNumber) =>
        PdfTextExtractor.GetTextFromPage(document.GetPage(pageNumber), new SimpleTextExtractionStrategy()) ?? "";

    public string ReadDeclaration(PdfDocument document, int pageNumber, double cropFraction, Func<string, string> extract)
    {
        string text = ReadText(document, pageNumber);
        string value = extract(text);
        return value.Length > 0 ? value : extract(RecognizePage(pageNumber, cropFraction));
    }

    public string RecognizePage(int pageNumber, double cropFraction = 1, Func<string, int, bool>? acceptOrientation = null)
    {
        renderer ??= PdfOcr.OpenPdfium().GetDocReader(path, new PageDimensions(2067, 2924));
        using var page = renderer.GetPageReader(pageNumber - 1);
        int width = page.GetPageWidth();
        int height = Math.Max(1, (int)(page.GetPageHeight() * cropFraction));
        byte[] bgra = page.GetImage();
        var grayscale = new byte[checked(width * height)];

        for (int i = 0, offset = 0; i < grayscale.Length; i++, offset += 4)
        {
            // PDFium renders on transparency. Composite on white before OCR.
            int luminance = (29 * bgra[offset] + 150 * bgra[offset + 1] + 77 * bgra[offset + 2]) >> 8;
            int alpha = bgra[offset + 3];
            grayscale[i] = (byte)((luminance * alpha + 255 * (255 - alpha)) / 255);
        }

        if (acceptOrientation is null)
            return ocr.Recognize(grayscale, width, height).Text;

        byte[]? rotated = null;
        var best = (Text: "", Confidence: -1, Correction: int.MaxValue);
        foreach (int turns in GetCorrections(grayscale, width, height))
        {
            var result = turns == 0
                ? ocr.Recognize(grayscale, width, height)
                : RecognizeRotated(grayscale, rotated ??= new byte[grayscale.Length], width, height, turns);
            if (acceptOrientation(result.Text, result.Confidence))
            {
                lastAcceptedCorrection = turns;
                return result.Text;
            }

            // Preserve the original orientation order when OCR confidence ties.
            if (result.Confidence > best.Confidence || (result.Confidence == best.Confidence && turns < best.Correction))
                best = (result.Text, result.Confidence, turns);
        }

        return best.Text;
    }

    private IEnumerable<int> GetCorrections(byte[] pixels, int width, int height)
    {
        int? previous = lastAcceptedCorrection;
        if (previous is int turns)
            yield return turns;

        int first = ocr.DetectCorrection(pixels, width, height) ?? previous ?? 0;
        for (int offset = 0; offset < 4; offset++)
        {
            int correction = (first + offset) % 4;
            if (correction != previous)
                yield return correction;
        }
    }

    private (string Text, int Confidence) RecognizeRotated(byte[] source, byte[] rotated, int width, int height, int turns)
    {
        if (turns == 2)
        {
            for (int i = 0; i < source.Length; i++)
                rotated[source.Length - 1 - i] = source[i];
            return ocr.Recognize(rotated, width, height);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int destination = turns == 1 ? x * height + height - 1 - y : (width - 1 - x) * height + y;
                rotated[destination] = source[y * width + x];
            }
        }

        return ocr.Recognize(rotated, height, width);
    }

    public void Dispose() => renderer?.Dispose();
}

internal sealed class TesseractHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle()
    {
        TesseractNative.Delete(handle);
        return true;
    }
}

internal static class TesseractNative
{
    private const string Library = "C0302Tesseract";

    static TesseractNative() => NativeLibrary.SetDllImportResolver(typeof(TesseractNative).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
            return 0;

        if (OperatingSystem.IsWindows())
        {
            var directory = Path.Combine(AppContext.BaseDirectory, Environment.Is64BitProcess ? "x64" : "x86");
            NativeLibrary.Load(Path.Combine(directory, "leptonica-1.82.0.dll"));
            return NativeLibrary.Load(Path.Combine(directory, "tesseract50.dll"));
        }

        return NativeLibrary.Load("libtesseract.so.5", assembly, searchPath);
    }

    [DllImport(Library, EntryPoint = "TessBaseAPICreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern TesseractHandle Create();

    [DllImport(Library, EntryPoint = "TessBaseAPIInit2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Init(TesseractHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string language, int mode);

    [DllImport(Library, EntryPoint = "TessBaseAPISetPageSegMode", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetPageSegMode(TesseractHandle handle, int mode);

    [DllImport(Library, EntryPoint = "TessBaseAPISetImage", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetImage(TesseractHandle handle, byte[] image, int width, int height, int bytesPerPixel, int stride);

    [DllImport(Library, EntryPoint = "TessBaseAPISetSourceResolution", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetSourceResolution(TesseractHandle handle, int ppi);

    [DllImport(Library, EntryPoint = "TessBaseAPIDetectOrientationScript", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DetectOrientation(TesseractHandle handle, out int degrees, out float orientationConfidence,
        out nint script, out float scriptConfidence);

    [DllImport(Library, EntryPoint = "TessBaseAPIGetUTF8Text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint GetUtf8Text(TesseractHandle handle);

    [DllImport(Library, EntryPoint = "TessBaseAPIMeanTextConf", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MeanTextConfidence(TesseractHandle handle);

    [DllImport(Library, EntryPoint = "TessBaseAPIClear", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Clear(TesseractHandle handle);

    [DllImport(Library, EntryPoint = "TessDeleteText", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DeleteText(nint text);

    [DllImport(Library, EntryPoint = "TessBaseAPIDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Delete(nint handle);
}
